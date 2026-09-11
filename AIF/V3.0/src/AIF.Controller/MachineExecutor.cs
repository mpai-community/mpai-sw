namespace AIF.Controller;

// Runs an AIM hierarchy.
//
// ROUTING IS BY DATATYPE. A Topology connection is two TYPED endpoints
// (Endpoint = AimName?, DataType, PortNumber) - resolved by the loader from the
// AMD's ExternalPorts / InternalTypes. Port NAMES do not exist here: nothing in
// this class addresses a port by a name. A boundary datum is keyed by its
// Endpoint.Key = "DataType#PortNumber"; ports of the same type are told apart
// only by PortNumber.
//
// SUSPEND / RESUME: a composite suspends when a required boundary input
// (DataType, PortNumber) has not been supplied for a consumer, and resumes when
// the User Agent supplies it. The UA deals only in typed data.
public sealed class MachineExecutor
{
    private readonly AimHost host;

    private readonly ExecutionPlanner planner =
        new();

    public MachineExecutor(
        AimHost host)
    {
        this.host = host;
    }

    public IReadOnlyList<string> Plan(
        DescriptorGraph graph)
    {
        return planner.BuildPlan(graph.Root);
    }

    // Single-pass entry point (throws if the run would suspend).
    public async Task<Message> ExecuteAsync(
        DescriptorGraph graph,
        Message message)
    {
        var result = await ExecuteNodeResumableAsync(
            graph.Root,
            planner.BuildPlan(graph.Root),
            0,
            new Dictionary<string, Dictionary<string, RoutedObject>>(),
            new Dictionary<string, string>(message.Ports),
            message);

        if (result.IsSuspended)
            throw new InvalidOperationException(
                "Composite suspended waiting for boundary input " +
                $"'{result.Suspended!.WaitingPort}'. Use ExecuteResumableAsync.");

        return result.Completed!;
    }

    // Resumable entry point.
    public Task<ExecutionResult> ExecuteResumableAsync(
        DescriptorGraph graph,
        Message message)
    {
        return ExecuteNodeResumableAsync(
            graph.Root,
            planner.BuildPlan(graph.Root),
            0,
            new Dictionary<string, Dictionary<string, RoutedObject>>(),
            new Dictionary<string, string>(message.Ports),
            message);
    }

    // Resume with more boundary input, keyed by (DataType, PortNumber) i.e. the
    // Endpoint.Key of the boundary port.
    public Task<ExecutionResult> ResumeAsync(
        SuspendedExecution suspended,
        IReadOnlyDictionary<string, string> addedBoundary)
    {
        var boundary =
            new Dictionary<string, string>(suspended.Boundary);

        foreach (var kv in addedBoundary)
            boundary[kv.Key] = kv.Value;

        return ExecuteNodeResumableAsync(
            suspended.Node,
            suspended.Plan,
            suspended.Position,
            suspended.Outputs,
            boundary,
            suspended.Envelope);
    }

    // Core resumable loop.
    private async Task<ExecutionResult> ExecuteNodeResumableAsync(
        DescriptorNode node,
        IReadOnlyList<string> plan,
        int startPosition,
        Dictionary<string, Dictionary<string, RoutedObject>> outputs,
        Dictionary<string, string> boundary,
        Message message)
    {
        var children =
            node.Children.ToDictionary(
                child => child.AIMName,
                child => child);

        Message last = message;

        for (int position = startPosition; position < plan.Count; position++)
        {
            var aimName = plan[position];
            var child   = children[aimName];

            var missing =
                MissingBoundaryInput(node, child, boundary, outputs);

            if (missing is not null)
            {
                var suspended = new SuspendedExecution
                {
                    Node            = node,
                    Plan            = plan,
                    Position        = position,
                    Outputs         = outputs,
                    Boundary        = boundary,
                    Envelope        = message,
                    WaitingAim      = aimName,
                    WaitingPort     = missing.Value.Key,
                    WaitingDataType = missing.Value.DataType,
                    PartialOutputs  = CollectOutputs(node, outputs, last)
                };
                return ExecutionResult.Suspend(suspended);
            }

            // Nothing suspended us, but this AIM may have nothing to work on -
            // e.g. an optional boundary input that was not supplied. Skip it.
            if (HasNoInputAvailable(node, child, boundary, outputs))
            {
                Console.WriteLine($"[AIF] {aimName}: skipped (no input available)");
                continue;
            }

            var inbox =
                BuildInbox(node, child, outputs, boundary);

            var input =
                new Message
                {
                    MessageId   = message.MessageId,
                    MessageType = message.MessageType,
                    Ports       = inbox
                };

            input.Inputs.AddRange(
                BuildInputs(node, child, outputs));

            Console.WriteLine(
                $"[AIF] {aimName}: Ports={input.Ports.Count}, Inputs={input.Inputs.Count}");

            Message result;
            try
            {
                result =
                    child.IsComposite
                    ? await RunCompositeChildAsync(child, input)
                    : await host.ProcessAsync(aimName, input);
            }
            catch (OperationCanceledException cancelled)
            {
                return ExecutionResult.Complete(
                    Message.Cancelled(message.MessageId, aimName, cancelled.Message));
            }
            catch (Exception failure)
            {
                return ExecutionResult.Complete(
                    Message.Error(message.MessageId, aimName, failure.Message));
            }

            if (result.IsError || result.IsCancelled)
                return ExecutionResult.Complete(result);

            // Store each output port tagged with the DataType it carries. For a
            // leaf, result.Ports is keyed by the leaf's own output port names, so
            // the DataType is read from the leaf's declared Ports. For a composite
            // child, result.Ports is keyed by the child's boundary Endpoint.Key
            // ("DataType#n"), so the DataType is the part before '#'.
            outputs[aimName] =
                result.Ports.ToDictionary(
                    port => port.Key,
                    port => new RoutedObject
                    {
                        DataType = child.IsComposite
                            ? port.Key.Split('#')[0]
                            : (child.Ports
                                   .FirstOrDefault(p => p.Direction == "Output" && p.Name == port.Key)?.DataType
                               ?? result.DataType),
                        Payload  = port.Value
                    });

            last = result;
        }

        return ExecutionResult.Complete(
            new Message
            {
                MessageId   = message.MessageId,
                MessageType = last.MessageType,
                DataType    = last.DataType,
                Payload     = last.Payload,
                Ports       = CollectOutputs(node, outputs, last)
            });
    }

    private async Task<Message> RunCompositeChildAsync(
        DescriptorNode child,
        Message input)
    {
        var result = await ExecuteNodeResumableAsync(
            child,
            planner.BuildPlan(child),
            0,
            new Dictionary<string, Dictionary<string, RoutedObject>>(),
            new Dictionary<string, string>(input.Ports),
            input);

        if (result.IsSuspended)
            throw new InvalidOperationException(
                $"Nested composite '{child.AIMName}' suspended; " +
                "nested suspension is not yet supported.");

        return result.Completed!;
    }

    // ---- Type-based routing helpers ----------------------------------------

    // The AIM's OWN input port name that carries (dataType, portNumber). Used to
    // key a leaf's inbox, because a leaf reads its Message.Ports by its own port
    // names (which it resolves from DataType via AimPortReader).
    private static string? InputPortForDataType(
        DescriptorNode aim, string dataType, int ordinal = 1) =>
        PortForDataType(aim, "Input", dataType, ordinal);

    private static string? OutputPortForDataType(
        DescriptorNode aim, string dataType, int ordinal = 1) =>
        PortForDataType(aim, "Output", dataType, ordinal);

    // Routing is by DataType; when one AIM declares several ports of the same
    // Direction and DataType the PortNumber decides (the port whose AMD
    // PortNumber equals it, else the n-th such port in declaration order).
    private static string? PortForDataType(
        DescriptorNode aim, string direction, string dataType, int ordinal)
    {
        var candidates = aim.Ports
            .Where(p => p.Direction == direction && p.Accepts(dataType))
            .ToList();

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0].Name;

        var declared = candidates.FirstOrDefault(p => p.PortNumber == ordinal);
        if (declared is not null) return declared.Name;

        return ordinal >= 1 && ordinal <= candidates.Count
            ? candidates[ordinal - 1].Name
            : null;
    }

    // The boundary (Endpoint.Key, DataType) that 'aim' requires but which is not
    // yet present, or null if all its boundary-sourced inputs are ready.
    private (string Key, string DataType)? MissingBoundaryInput(
        DescriptorNode node,
        DescriptorNode aim,
        IReadOnlyDictionary<string, string> boundary,
        IReadOnlyDictionary<string, Dictionary<string, RoutedObject>> outputs)
    {
        foreach (var connection in node.Connections)
        {
            if (connection.Input.AimName != aim.AIMName)
                continue;

            var source = connection.Output;
            if (source.AimName is not null)
                continue;   // AIM-to-AIM inputs are produced within the run

            if (!boundary.ContainsKey(source.Key))
            {
                var dt = source.DataType;

                if (InternallySatisfied(node, aim, dt, outputs))
                    continue;   // fed by an AIM that has produced this DataType

                if (BoundaryPortIsOptional(node, dt, source.PortNumber))
                    continue;   // nobody is coming; skip rather than wait

                return (source.Key, dt);
            }
        }

        return null;
    }

    // True if the composite boundary INPUT of (dataType, portNumber) is optional.
    private static bool BoundaryPortIsOptional(DescriptorNode node, string dataType, int portNumber) =>
        node.Ports.Any(p =>
            p.Direction == "Input" && p.Accepts(dataType) &&
            (p.PortNumber ?? 1) == portNumber && p.IsOptional);

    // True if 'aim' would run with NO input at all: every input is either an
    // unsupplied optional boundary port, or an internal connection whose producer
    // has not produced. Such an AIM is skipped.
    private bool HasNoInputAvailable(
        DescriptorNode node,
        DescriptorNode aim,
        IReadOnlyDictionary<string, string> boundary,
        IReadOnlyDictionary<string, Dictionary<string, RoutedObject>> outputs)
    {
        var any = false;

        foreach (var connection in node.Connections)
        {
            if (connection.Input.AimName != aim.AIMName) continue;

            var source = connection.Output;

            if (source.AimName is null)
            {
                if (boundary.ContainsKey(source.Key)) { any = true; break; }
                continue;
            }

            if (outputs.TryGetValue(source.AimName, out var produced) &&
                FindProduced(produced, source.DataType) is not null)
            {
                any = true;
                break;
            }
        }

        return !any;
    }

    // True if 'aim' has an AIM-to-AIM input connection carrying 'dataType' whose
    // source AIM has already produced an output of that DataType.
    private bool InternallySatisfied(
        DescriptorNode node,
        DescriptorNode aim,
        string dataType,
        IReadOnlyDictionary<string, Dictionary<string, RoutedObject>> outputs)
    {
        if (string.IsNullOrEmpty(dataType)) return false;

        foreach (var connection in node.Connections)
        {
            if (connection.Input.AimName != aim.AIMName)
                continue;

            var source = connection.Output;
            if (source.AimName is null)
                continue;   // boundary source, not internal

            if (source.DataType != dataType)
                continue;

            if (outputs.TryGetValue(source.AimName, out var producedPorts) &&
                FindProduced(producedPorts, dataType) is not null)
                return true;
        }

        return false;
    }

    // Structured inputs (DataObjectMessage list) for AIM-to-AIM connections.
    private List<DataObjectMessage> BuildInputs(
        DescriptorNode node,
        DescriptorNode aim,
        IReadOnlyDictionary<string, Dictionary<string, RoutedObject>> outputs)
    {
        var inputs = new List<DataObjectMessage>();

        foreach (var connection in node.Connections)
        {
            if (connection.Input.AimName != aim.AIMName)
                continue;

            var source = connection.Output;
            if (source.AimName is null)
                continue;   // boundary handled in BuildInbox

            var dataType = source.DataType;

            if (!outputs.TryGetValue(source.AimName, out var producedPorts))
                continue;

            var routed = FindProduced(producedPorts, dataType);
            if (routed is null)
                continue;

            inputs.Add(new DataObjectMessage { DataType = dataType, Payload = routed.Payload });
        }

        return inputs;
    }

    // The inbox for 'aim'. A LEAF is keyed by its own input port NAMES (the leaf
    // reads Message.Ports by name, resolved from DataType via AimPortReader). A
    // COMPOSITE child is keyed by the boundary Endpoint.Key ("DataType#n"),
    // because the nested executor reads its boundary by (DataType, PortNumber).
    //
    // When a destination is fed by BOTH a boundary source and an internal AIM
    // source, the BOUNDARY value wins when present.
    private Dictionary<string, string> BuildInbox(
        DescriptorNode node,
        DescriptorNode aim,
        IReadOnlyDictionary<string, Dictionary<string, RoutedObject>> outputs,
        IReadOnlyDictionary<string, string> boundary)
    {
        var inbox  = new Dictionary<string, string>();
        var filled = new HashSet<string>();

        string DestKey(Endpoint consumer) =>
            aim.IsComposite
                ? consumer.Key
                : (InputPortForDataType(aim, consumer.DataType, consumer.PortNumber) ?? consumer.Key);

        // Pass 1: boundary sources (explicit human inputs) - highest priority.
        foreach (var connection in node.Connections)
        {
            if (connection.Input.AimName != aim.AIMName) continue;

            var source = connection.Output;
            if (source.AimName is not null) continue;   // internal handled in pass 2

            var destKey = DestKey(connection.Input);
            if (boundary.TryGetValue(source.Key, out var supplied))
            {
                inbox[destKey] = supplied;
                filled.Add(destKey);
            }
        }

        // Pass 2: internal AIM sources - fill only destinations not set above.
        foreach (var connection in node.Connections)
        {
            if (connection.Input.AimName != aim.AIMName) continue;

            var source = connection.Output;
            if (source.AimName is null) continue;   // boundary handled in pass 1

            var destKey = DestKey(connection.Input);
            if (filled.Contains(destKey)) continue;

            if (outputs.TryGetValue(source.AimName, out var producedPorts))
            {
                var routed = FindProduced(producedPorts, source.DataType);
                if (routed is not null)
                    inbox[destKey] = routed.Payload;
            }
        }

        return inbox;
    }

    // What the composite exposes on its boundary outputs, keyed by the boundary
    // output Endpoint.Key ("DataType#n"), so the User Agent reads outputs by
    // (DataType, PortNumber).
    private Dictionary<string, string> CollectOutputs(
        DescriptorNode node,
        IReadOnlyDictionary<string, Dictionary<string, RoutedObject>> outputs,
        Message last)
    {
        var composite = new Dictionary<string, string>();

        foreach (var connection in node.Connections)
        {
            var source = connection.Output;   // producing AIM
            var dest   = connection.Input;    // boundary

            if (dest.AimName is not null || source.AimName is null)
                continue;   // only AIM -> boundary

            var dataType = source.DataType;

            if (outputs.TryGetValue(source.AimName, out var producedPorts))
            {
                var routed = FindProduced(producedPorts, dataType);
                if (routed is not null)
                    composite[dest.Key] = routed.Payload;
            }
        }

        return composite.Count > 0
            ? composite
            : new Dictionary<string, string>(last.Ports);
    }

    // Find a produced object of the given DataType among an AIM's output ports.
    private static RoutedObject? FindProduced(
        Dictionary<string, RoutedObject> producedPorts,
        string dataType)
    {
        foreach (var kv in producedPorts)
            if (kv.Value.DataType == dataType)
                return kv.Value;
        return producedPorts.Count == 1 ? producedPorts.Values.First() : null;
    }
}
