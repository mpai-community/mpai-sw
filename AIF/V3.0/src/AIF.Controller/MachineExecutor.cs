namespace AIF.Controller;

// Runs an AIM hierarchy.
//
// ROUTING IS BY DATATYPE, not by port-name string matching. A Topology
// connection names two endpoints (an AIM and, advisorily, a port). What
// actually flows is a DataType. For each connection the executor resolves:
//   * the SOURCE AIM's output port carrying the connection's DataType, and
//   * the DEST  AIM's input  port carrying the same DataType,
// from each AIM's own declared Ports (name<->DataType<->direction). The
// Topology's port-name strings are used only to disambiguate when a pair of
// AIMs is joined by more than one connection of different DataTypes; otherwise
// the DataType alone determines the ports. This means an AIM's internal port
// names never have to agree with the names used in a composite's Topology.
//
// SUSPEND / RESUME (general AIF capability): a composite suspends when a
// required boundary input DataType has not been supplied for a consumer, and
// resumes when the User Agent writes it. The UA deals only in boundary ports
// and data; it never names an AIM nor orders execution.
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

    // ── Single-pass entry point (throws if the run would suspend) ────────────
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
                "Composite suspended waiting for boundary input port " +
                $"'{result.Suspended!.WaitingPort}'. Use ExecuteResumableAsync.");

        return result.Completed!;
    }

    // ── Resumable entry point ────────────────────────────────────────────────
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

    // Resume with more boundary input, keyed by boundary PORT NAME.
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

    // ── Core resumable loop ──────────────────────────────────────────────────
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
                    WaitingPort     = missing.Value.Port,
                    WaitingDataType = missing.Value.DataType,
                    PartialOutputs  = CollectOutputs(node, outputs, last)
                };
                return ExecutionResult.Suspend(suspended);
            }

            // Nothing suspended us, but this AIM may have nothing to work on -
            // e.g. ASR in a text-only request, where InputSpeech is declared
            // IsOptional and was not supplied. Skip it and carry on; its
            // consumers take their input from the other branch.
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

            // Store each output port tagged with the DataType THAT PORT carries,
            // resolved from the AIM's own declared Ports - not the Message's
            // single top-level DataType. An AIM (e.g. TIQ) may emit several
            // ports of different DataTypes; tagging them all with one DataType
            // would collide. Routing downstream is by DataType, so each port
            // must carry its true DataType.
            outputs[aimName] =
                result.Ports.ToDictionary(
                    port => port.Key,
                    port => new RoutedObject
                    {
                        DataType = child.Ports
                                       .FirstOrDefault(p => p.Direction == "Output" && p.Name == port.Key)?.DataType
                                   ?? result.DataType,
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

    // ── DataType-based routing helpers ───────────────────────────────────────

    // The DataType a connection carries, resolved from the producing endpoint.
    // Prefer the source AIM's output port whose NAME matches the Topology's
    // source port name; if the name does not match (the Topology used a
    // different vocabulary), fall back to the AIM's sole output DataType that
    // the destination also consumes.
    private static string? ConnectionDataType(
        DescriptorNode node,
        TopologyConnection connection,
        IReadOnlyDictionary<string, DescriptorNode> children)
    {
        var source = Endpoint.Parse(connection.Source);
        var dest   = Endpoint.Parse(connection.Destination);

        // Boundary source: DataType from the composite's own ExternalPort.
        if (source.AimName is null)
            return PortDataType(node, source.PortName, "Input")
                ?? PortDataType(node, source.PortName, null);

        if (!children.TryGetValue(source.AimName, out var srcNode))
            return null;

        // 1. Exact port-name match on the source's outputs.
        var byName = srcNode.Ports
            .FirstOrDefault(p => p.Direction == "Output" && p.Name == source.PortName);
        if (byName is not null) return byName.DataType;

        // 2. Fall back: the DataType the source outputs and the dest consumes.
        if (dest.AimName is not null && children.TryGetValue(dest.AimName, out var dstNode))
        {
            // Over the SETS a Port accepts, not over one Data Type each: a
            // source emitting either kind and a destination accepting either
            // share both, and the connection is ambiguous only if they share
            // more than one AND nothing else settles it.
            var shared = srcNode.Ports.Where(p => p.Direction == "Output")
                .SelectMany(p => p.DataTypes.Count > 0 ? p.DataTypes : new[] { p.DataType })
                .Distinct()
                .Intersect(dstNode.Ports.Where(p => p.Direction == "Input")
                    .SelectMany(p => p.DataTypes.Count > 0 ? p.DataTypes : new[] { p.DataType })
                    .Distinct())
                .ToList();
            if (shared.Count == 1) return shared[0];
        }

        // 3. Dest is the boundary: match the composite's output ExternalPort.
        if (dest.AimName is null)
        {
            var dtype = PortDataType(node, dest.PortName, "Output");
            if (dtype is not null &&
                srcNode.Ports.Any(p => p.Direction == "Output" && p.Accepts(dtype)))
                return dtype;
        }

        // 4. Single output DataType — unambiguous.
        var outs = srcNode.Ports.Where(p => p.Direction == "Output")
            .SelectMany(p => p.DataTypes.Count > 0 ? p.DataTypes : new[] { p.DataType })
            .Distinct().ToList();
        return outs.Count == 1 ? outs[0] : null;
    }

    private static string? PortDataType(DescriptorNode node, string portName, string? direction)
    {
        var p = node.Ports.FirstOrDefault(x =>
            x.Name == portName && (direction is null || x.Direction == direction));
        return p?.DataType;
    }

    // The input port name on 'aim' that carries the given DataType.
    private static string? InputPortForDataType(
        DescriptorNode aim, string dataType, int ordinal = 1) =>
        PortForDataType(aim, "Input", dataType, ordinal);

    // The output port name on 'aim' that carries the given DataType.
    private static string? OutputPortForDataType(
        DescriptorNode aim, string dataType, int ordinal = 1) =>
        PortForDataType(aim, "Output", dataType, ordinal);

    // Routing is by DataType. When that is unambiguous - one port of this
    // Direction and DataType - the ordinal is irrelevant and ignored, so every
    // existing AMD keeps working unchanged.
    //
    // When an AIM declares SEVERAL ports of the same Direction and DataType,
    // DataType alone cannot say which is meant and the ordinal decides:
    // the port whose AMD PortNumber equals it, or failing that the n-th such
    // port in declaration order. Returning null (rather than the first port)
    // when the ordinal is out of range is deliberate: silently delivering to
    // the wrong port of the right type is the failure this exists to prevent.
    private static string? PortForDataType(
        DescriptorNode aim, string direction, string dataType, int ordinal)
    {
        // ACCEPTS, not equals. A Port may declare the SET of Data Types it
        // takes - a Port receiving either a Basic or a full Audio Object
        // declares both - and the AIM Metadata states the rule: a value routes
        // to a Port whose set CONTAINS its Data Type. Equality was the rule
        // while a Port could carry only one.
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

    // Returns the boundary (port, DataType) that 'aim' requires but which is not
    // yet present, or null if all its boundary-sourced inputs are ready.
    private (string Port, string DataType)? MissingBoundaryInput(
        DescriptorNode node,
        DescriptorNode aim,
        IReadOnlyDictionary<string, string> boundary,
        IReadOnlyDictionary<string, Dictionary<string, RoutedObject>> outputs)
    {
        var children = node.Children.ToDictionary(c => c.AIMName, c => c);

        foreach (var connection in node.Connections)
        {
            var destination = Endpoint.Parse(connection.Destination);
            if (destination.AimName != aim.AIMName)
                continue;

            var source = Endpoint.Parse(connection.Source);
            if (source.AimName is not null)
                continue;   // AIM-to-AIM inputs are produced within the run

            // Boundary-sourced. Normally the composite boundary port must have a
            // value. BUT if the SAME destination port is ALSO fed by an internal
            // AIM connection whose producer has already run (its output of the
            // matching DataType is available), then the question/input arrived
            // internally (e.g. TIQ.InputText fed by ASR in voice mode), and the
            // boundary value is optional — do not suspend for it.
            if (!boundary.ContainsKey(source.PortName))
            {
                var dt = PortDataType(node, source.PortName, "Input") ?? string.Empty;

                if (InternallySatisfied(node, aim, dt, outputs))
                    continue;   // fed by an AIM that has produced this DataType

                // An OPTIONAL boundary input that was not supplied is not a
                // reason to wait: nobody is coming. Report it as skippable and
                // let the caller decide, rather than suspending forever.
                if (BoundaryPortIsOptional(node, source.PortName))
                    continue;

                return (source.PortName, dt);
            }
        }

        return null;
    }

    // True if the named composite boundary INPUT port is declared IsOptional.
    private static bool BoundaryPortIsOptional(DescriptorNode node, string portName) =>
        node.Ports.Any(p =>
            p.Direction == "Input" && p.Name == portName && p.IsOptional);

    // True if 'aim' would run with NO input at all: every one of its inputs is
    // either an unsupplied optional boundary port, or an internal connection
    // whose producer has not produced. Such an AIM is skipped.
    //
    // This is deliberately narrow. An AIM with even one input available RUNS;
    // only a wholly starved AIM is skipped. Combined with IsOptional, that is
    // what lets ASR sit out a text-only request while still being suspended for
    // in a workflow that really is waiting on speech.
    private bool HasNoInputAvailable(
        DescriptorNode node,
        DescriptorNode aim,
        IReadOnlyDictionary<string, string> boundary,
        IReadOnlyDictionary<string, Dictionary<string, RoutedObject>> outputs)
    {
        var children = node.Children.ToDictionary(c => c.AIMName, c => c);
        var any      = false;

        foreach (var connection in node.Connections)
        {
            var destination = Endpoint.Parse(connection.Destination);
            if (destination.AimName != aim.AIMName) continue;

            var source = Endpoint.Parse(connection.Source);

            if (source.AimName is null)
            {
                if (boundary.ContainsKey(source.PortName)) { any = true; break; }
                continue;
            }

            var dataType = ConnectionDataType(node, connection, children);
            if (dataType is null) continue;

            if (outputs.TryGetValue(source.AimName, out var produced) &&
                FindProduced(produced, dataType) is not null)
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
        var children = node.Children.ToDictionary(c => c.AIMName, c => c);

        foreach (var connection in node.Connections)
        {
            var destination = Endpoint.Parse(connection.Destination);
            if (destination.AimName != aim.AIMName)
                continue;

            var source = Endpoint.Parse(connection.Source);
            if (source.AimName is null)
                continue;   // boundary source, not internal

            var connDataType = ConnectionDataType(node, connection, children);
            if (connDataType != dataType)
                continue;

            if (outputs.TryGetValue(source.AimName, out var producedPorts) &&
                FindProduced(producedPorts, dataType) is not null)
                return true;
        }

        return false;
    }

    // Structured inputs (DataObjectMessage list) for AIM-to-AIM connections,
    // resolved by DataType.
    private List<DataObjectMessage> BuildInputs(
        DescriptorNode node,
        DescriptorNode aim,
        IReadOnlyDictionary<string, Dictionary<string, RoutedObject>> outputs)
    {
        var children = node.Children.ToDictionary(c => c.AIMName, c => c);
        var inputs   = new List<DataObjectMessage>();

        foreach (var connection in node.Connections)
        {
            var destination = Endpoint.Parse(connection.Destination);
            if (destination.AimName != aim.AIMName)
                continue;

            var source = Endpoint.Parse(connection.Source);
            if (source.AimName is null)
                continue;   // boundary handled in BuildInbox

            var dataType = ConnectionDataType(node, connection, children);
            if (dataType is null) continue;

            if (!outputs.TryGetValue(source.AimName, out var producedPorts))
            {
                Console.WriteLine($"[AIF] INPUT MISS AIM: {source.AimName}");
                continue;
            }

            // Find the produced payload of this DataType (by the source's
            // output port name for that DataType, or any port carrying it).
            var routed = FindProduced(producedPorts, dataType);
            if (routed is null)
            {
                Console.WriteLine($"[AIF] INPUT MISS DATATYPE: {source.AimName} -> {dataType}");
                continue;
            }

            Console.WriteLine($"[AIF] INPUT HIT: {source.AimName} -> {dataType}");
            inputs.Add(new DataObjectMessage { DataType = dataType, Payload = routed.Payload });
        }

        return inputs;
    }

    // The inbox (destination-port-name -> payload) for 'aim', resolved by
    // DataType for both boundary and AIM-to-AIM connections.
    //
    // When the SAME destination port is fed by BOTH a boundary source and an
    // internal AIM source (e.g. TIQ.InputText from the boundary typed text AND
    // from ASR), the BOUNDARY value wins when present: it is the explicit human
    // input for this run. Only when the boundary value is absent is the internal
    // (AIM-produced) value used. This makes text mode use the typed question and
    // voice mode use the ASR transcription, deterministically.
    private Dictionary<string, string> BuildInbox(
        DescriptorNode node,
        DescriptorNode aim,
        IReadOnlyDictionary<string, Dictionary<string, RoutedObject>> outputs,
        IReadOnlyDictionary<string, string> boundary)
    {
        var children      = node.Children.ToDictionary(c => c.AIMName, c => c);
        var inbox         = new Dictionary<string, string>();
        var fromBoundary  = new HashSet<string>();

        // Pass 1: boundary sources (explicit human inputs) — highest priority.
        foreach (var connection in node.Connections)
        {
            var destination = Endpoint.Parse(connection.Destination);
            if (destination.AimName != aim.AIMName) continue;

            var source = Endpoint.Parse(connection.Source);
            if (source.AimName is not null) continue;   // internal handled in pass 2

            var dataType = ConnectionDataType(node, connection, children);
            if (dataType is null) continue;

            var destPort =
                InputPortForDataType(aim, dataType, destination.PortNumber)
                ?? destination.PortName;
            if (boundary.TryGetValue(source.PortName, out var supplied))
            {
                inbox[destPort] = supplied;
                fromBoundary.Add(destPort);
            }
        }

        // Pass 2: internal AIM sources — fill only ports not already set by a
        // boundary value.
        foreach (var connection in node.Connections)
        {
            var destination = Endpoint.Parse(connection.Destination);
            if (destination.AimName != aim.AIMName) continue;

            var source = Endpoint.Parse(connection.Source);
            if (source.AimName is null) continue;   // boundary handled in pass 1

            var dataType = ConnectionDataType(node, connection, children);
            if (dataType is null) continue;

            var destPort =
                InputPortForDataType(aim, dataType, destination.PortNumber)
                ?? destination.PortName;
            if (fromBoundary.Contains(destPort)) continue;   // boundary wins

            if (outputs.TryGetValue(source.AimName, out var producedPorts))
            {
                var routed = FindProduced(producedPorts, dataType);
                if (routed is not null)
                    inbox[destPort] = routed.Payload;
            }
        }

        return inbox;
    }

    // What the composite exposes on its boundary outputs, resolved by DataType.
    private Dictionary<string, string> CollectOutputs(
        DescriptorNode node,
        IReadOnlyDictionary<string, Dictionary<string, RoutedObject>> outputs,
        Message last)
    {
        var children  = node.Children.ToDictionary(c => c.AIMName, c => c);
        var composite = new Dictionary<string, string>();

        foreach (var connection in node.Connections)
        {
            var source      = Endpoint.Parse(connection.Source);
            var destination = Endpoint.Parse(connection.Destination);

            // Boundary OUTPUT: dest is the composite boundary (no AIM name),
            // source is an AIM.
            if (destination.AimName is not null || source.AimName is null)
                continue;

            var dataType = ConnectionDataType(node, connection, children);
            if (dataType is null) continue;

            if (outputs.TryGetValue(source.AimName, out var producedPorts))
            {
                var routed = FindProduced(producedPorts, dataType);
                if (routed is not null)
                    composite[destination.PortName] = routed.Payload;
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
        // Fall back: a single produced port.
        return producedPorts.Count == 1 ? producedPorts.Values.First() : null;
    }

    private readonly record struct Endpoint(
        string? AimName,
        string PortName,
        int PortNumber = 1)
    {
        // Accepts "Port", "AIM.Port", "Port#2", "AIM.Port#2".
        // The '#n' suffix is the Topology PortNumber; absent means 1.
        // Note AIM names themselves contain '.' (as in "MMC-TTT-V2.5"), which
        // is why the AIM/port split is on the LAST dot.
        public static Endpoint Parse(
            string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return new Endpoint(null, string.Empty);
            }

            var ordinal = 1;
            var hash    = endpoint.LastIndexOf('#');
            if (hash > 0 &&
                int.TryParse(endpoint[(hash + 1)..], out var parsedOrdinal) &&
                parsedOrdinal >= 1)
            {
                ordinal  = parsedOrdinal;
                endpoint = endpoint[..hash];
            }

            var separator =
                endpoint.LastIndexOf('.');

            return separator <= 0
                ? new Endpoint(null, endpoint, ordinal)
                : new Endpoint(
                      endpoint[..separator],
                      endpoint[(separator + 1)..],
                      ordinal);
        }
    }
}
