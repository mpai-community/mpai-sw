using AIF.Channels;

namespace AIF.Controller;

// THE CONTINUOUS EXECUTOR (M3205 5.2, M3215 3.3). Each AIM of the Module runs in a
// task of its own from Start to Stop, reading its Input Ports when Messages are
// pending and writing its Output Ports when it has something to write. Every way
// data moves is a Channel (3.1): one per Output Port the Topology connects, from
// its writer to every Input Port it reaches - through the boundaries of nested
// composites, which have no existence at run time - and to the Module's boundary
// Output Ports. The boundary Input Ports are Channels written by the User Agent.
//
// An AIM that is an IAimRunner is given its Ports; an AIM that is only an
// IAimProcessor is fired by the adapter: when a Message is pending on every
// required input Port connected in the Module and on at least one input Port,
// taking what is pending on the optional ones (M3205 5.3).
public sealed partial class ContinuousExecutor
{
    private readonly DescriptorGraph graph;
    private readonly AimHost host;
    private readonly IReadOnlyDictionary<string, IChannelTransport> transports;
    private readonly string module;

    private readonly List<ChannelSpec> channels = new();
    private readonly Dictionary<PortEnd, List<IChannelWriter>> writers = new();
    private readonly Dictionary<PortEnd, List<IChannelReader>> readers = new();
    private readonly List<Task> running = new();
    private readonly Dictionary<string, int> restarts = new();
    private readonly IClock clock;

    // Deadlines missed, by AIM: in all, and in a row (M3215 3.5).
    private readonly Dictionary<string, (int All, int InARow)> misses = new();

    // SOMEWHERE TO LOOK, WITHOUT KNOWING WHAT IS BEING LOOKED AT. The Framework
    // routes Data Types and payloads; it does not know what an MPAI Object is, and
    // must not. Whoever knows both worlds installs the inspector - the Controller
    // API does - and the Controller transport shows it every Message it relays.
    // (AIM name, Data Type, payload). No inspector, no cost beyond a null check.
    public static Action<string, string, string>? ObjectInspector { get; set; }

    // A boundary Output Port the User Agent does not read must not stall the
    // Module: its reader end keeps the newest Messages.
    public static PortBehaviour BoundaryOutput { get; } = new(PortBehaviour.DefaultDepth, Overflow.DropOldest, null);

    // runsHere: whether a leaf AIM - or, for "", the boundary - runs on this
    // machine; an AIM that does not is on an AIM host (M3217 3.4), and every
    // Channel it writes or reads crosses the Remote transport. Null: all here.
    public ContinuousExecutor(
        DescriptorGraph graph,
        AimHost host,
        IReadOnlyDictionary<string, IChannelTransport> transports,
        string moduleInstance,
        string defaultTransport = "Controller",
        IClock? clock = null,
        Func<string, bool>? runsHere = null)
    {
        this.graph = graph;
        this.host = host;
        this.transports = transports;
        this.clock = clock ?? SystemClock.Instance;
        this.runsHere = runsHere ?? (_ => true);
        module = moduleInstance;
        Payloads = new PayloadStore(this.clock);
        Plan(defaultTransport);

        // WHAT CROSSES THE BOUNDARY, TAKEN FOR THE RECORD AS IT IS STAMPED (M3219 3.4):
        // a Message written at a boundary Input Port, and one written for a boundary
        // Output Port, recorded in the one step that stamps it, so that the record's
        // sequence is the order of the stamps. A Message stamped on another machine -
        // written by an AIM on a host - is recorded as it reaches the boundary here.
        foreach (var transport in transports.Values.OfType<ChannelTransport>())
        {
            var stampedBefore = transport.Stamped;
            transport.Stamped = (spec, message) =>
            {
                stampedBefore?.Invoke(spec, message);
                if (spec.Module != module || Record is not { } record) return;
                if (spec.Writer.IsBoundary && message.RecordAsInput) record.Take("In", message);
                foreach (var reader in spec.Readers.Where(r => r.Reader.IsBoundary))
                    record.Take("Out", message, reader.Reader);
            };
            var before = transport.Delivered;
            transport.Delivered = (spec, reader, message) =>
            {
                before?.Invoke(spec, reader, message);
                if (spec.Module == module && reader.IsBoundary && !this.runsHere(spec.Writer.Aim))
                    Record?.Take("Out", message, reader);
            };
        }
    }

    // The record of the boundary, while there is one (M3219 3.4).
    public BoundaryRecord? Record { get; set; }

    // ON AN AIM HOST (M3217 3.1): the AIMs of a Module instance placed there, run
    // on the Channels its Controller planned - those they write or read - with
    // the policy of the composite that contains each. The Controller has the
    // graph; the host has only these.
    public ContinuousExecutor(
        IEnumerable<(DescriptorNode Leaf, string OnDegraded)> placed,
        IEnumerable<ChannelSpec> channelsPlanned,
        AimHost host,
        RemoteTransport remote,
        string moduleInstance,
        IClock? clock = null)
    {
        graph = new DescriptorGraph { Root = new DescriptorNode { AIMName = moduleInstance } };
        this.host = host;
        transports = new Dictionary<string, IChannelTransport> { [remote.Name] = remote };
        this.clock = clock ?? SystemClock.Instance;
        module = moduleInstance;
        Payloads = new PayloadStore(this.clock);
        foreach (var (leaf, onDegraded) in placed)
        {
            leaves[leaf.AIMName] = leaf;
            parent[leaf] = new DescriptorNode { AIMName = moduleInstance, OnDegraded = onDegraded };
        }
        runsHere = leaves.ContainsKey;                                  // the boundary ("") is the Controller's
        channels.AddRange(channelsPlanned);
    }

    private readonly Func<string, bool> runsHere;

    // Told of every AIM of this executor that is DEGRADED: (AIM, reason, policy).
    // A host tells the Controller, which applies the policy to the Module.
    public Action<string, string, string>? OnDegradedNotice { get; set; }

    // An AIM on a host is DEGRADED there: its composite's policy applies here.
    public void DegradedElsewhere(string aim, string reason)
    {
        if (leaves.TryGetValue(aim, out var leaf)) Degraded(leaf, reason);
    }

    // What an AIM host is to run of this Module: the Channels that touch the AIMs
    // given, and the policy of the composite containing each.
    public (IReadOnlyList<ChannelSpec> Channels, IReadOnlyList<(string Aim, string OnDegraded)> Aims) PlacedPart(IReadOnlySet<string> aims) =>
        (channels.Where(c => aims.Contains(c.Writer.Aim) || c.Readers.Any(r => aims.Contains(r.Reader.Aim))).ToList(),
         aims.Where(leaves.ContainsKey).Select(a => (a, parent[leaves[a]].OnDegraded)).ToList());

    // The payload store of this Module instance (M3215 3.6).
    public PayloadStore Payloads { get; }

    // The leaf AIMs, as the Controller checks them at Start (M3215 3.5).
    public IEnumerable<DescriptorNode> Aims => leaves.Values;

    public int DeadlinesMissed(string aim)
    {
        lock (misses) return misses.GetValueOrDefault(aim).All;
    }

    public IReadOnlyList<ChannelSpec> Channels => channels;

    // ---- the Channels of the Module ----------------------------------------

    // A terminal end: a leaf AIM's Port, or a Port of the Module's boundary.
    private readonly record struct Terminal(string Aim, string DataType, int PortNumber);

    private readonly Dictionary<DescriptorNode, DescriptorNode> parent = new();
    private readonly Dictionary<string, DescriptorNode> leaves = new();

    private void Plan(string defaultTransport)
    {
        Index(graph.Root);

        // Every writer: each leaf's Output Ports as the Topology names them, and
        // the boundary Input Ports of the Module.
        var byWriter = new Dictionary<PortEnd, HashSet<Terminal>>();
        void Add(PortEnd writer, IEnumerable<Terminal> sinks)
        {
            if (!byWriter.TryGetValue(writer, out var set)) byWriter[writer] = set = new HashSet<Terminal>();
            set.UnionWith(sinks);
        }

        foreach (var node in Composites(graph.Root))
            foreach (var connection in node.Connections)
            {
                var from = connection.Output;
                if (from.AimName is null)
                {
                    if (!ReferenceEquals(node, graph.Root)) continue;          // entering a nested composite: followed from outside
                    Add(new PortEnd("", from.DataType, from.PortNumber), Sinks(node, from));
                }
                else if (leaves.TryGetValue(from.AimName, out var leaf))
                {
                    var port = OwnPort(leaf, "Output", from.DataType, from.PortNumber);
                    Add(new PortEnd(leaf.AIMName, port?.DataType ?? from.DataType, port is null ? from.PortNumber : NumberOf(leaf, port)),
                        Sinks(node, from));
                }
                // An output of a nested composite is followed from its inside.
            }

        // A WRITER THAT NEVER WRITES (M3243 3.7, found in Step 4). An AIM the adapter
        // fires, no Input Port of which is connected, is never fired: what it would
        // write is not connected either, and a Port that would read it waits for
        // nothing. Followed through: in a CAV whose cabin has no camera, the visual
        // branch of HCI writes nothing, and the audio-visual alignment fires on the
        // audio alone.
        var silent = new HashSet<string>();
        for (var changed = true; changed;)
        {
            changed = false;
            var fed = byWriter.Where(w => w.Key.IsBoundary || !silent.Contains(w.Key.Aim)).SelectMany(w => w.Value).Select(t => t.Aim).ToHashSet();
            foreach (var leaf in leaves.Values)
            {
                if (silent.Contains(leaf.AIMName) || fed.Contains(leaf.AIMName) || leaf.Period is not null || !runsHere(leaf.AIMName)) continue;
                if (host.Processor(leaf.AIMName) is null or IAimRunner) continue;
                silent.Add(leaf.AIMName);
                changed = true;
            }
        }
        foreach (var writer in byWriter.Keys.Where(w => !w.IsBoundary && silent.Contains(w.Aim)).ToList()) byWriter.Remove(writer);

        foreach (var (writer, sinks) in byWriter)
        {
            // A Channel that touches an AIM on another machine crosses the Remote
            // transport, whatever its Port would choose on one machine.
            var crosses = (!writer.IsBoundary && !runsHere(writer.Aim)) || sinks.Any(s => s.Aim.Length > 0 && !runsHere(s.Aim));
            var transport = crosses
                ? "Remote"
                : writer.IsBoundary
                ? defaultTransport
                : OwnPort(leaves[writer.Aim], "Output", writer.DataType, writer.PortNumber)?.Transport ?? defaultTransport;
            if (!transports.ContainsKey(transport))
                throw new InvalidOperationException(
                    $"{module}: the Channel of {writer} is to use the {transport} transport, which this Controller does not have.");

            var specs = new List<ChannelReaderSpec>();
            foreach (var sink in sinks)
            {
                var end = new PortEnd(sink.Aim, sink.DataType, sink.PortNumber);
                if (sink.Aim.Length == 0) { specs.Add(new ChannelReaderSpec(end, BoundaryOutput)); continue; }

                var port = OwnPort(leaves[sink.Aim], "Input", sink.DataType, sink.PortNumber);
                if (port?.AcceptedTransports is { } accepted && !accepted.Contains(transport))
                    throw new InvalidOperationException(
                        $"{module}: {end} accepts {string.Join(", ", accepted)}; the Channel of {writer} it reads uses {transport}.");
                specs.Add(new ChannelReaderSpec(end, BehaviourOf(port)));
            }
            channels.Add(new ChannelSpec(module, writer, specs, transport));
        }
    }

    private void Index(DescriptorNode node)
    {
        foreach (var child in node.Children)
        {
            parent[child] = node;
            if (child.IsComposite) Index(child);
            else leaves[child.AIMName] = child;
        }
    }

    private static IEnumerable<DescriptorNode> Composites(DescriptorNode node)
    {
        yield return node;
        foreach (var child in node.Children.Where(c => c.IsComposite))
            foreach (var inner in Composites(child)) yield return inner;
    }

    // Where a flow leaving 'from' in 'node' arrives: leaf Input Ports and the
    // Module's boundary Output Ports, through nested composites in both directions.
    private IEnumerable<Terminal> Sinks(DescriptorNode node, Endpoint from)
    {
        foreach (var connection in node.Connections.Where(c => c.Output == from))
        {
            var to = connection.Input;
            if (to.AimName is null)
            {
                if (ReferenceEquals(node, graph.Root)) yield return new Terminal("", to.DataType, to.PortNumber);
                else foreach (var t in Sinks(parent[node], new Endpoint(node.AIMName, to.DataType, to.PortNumber))) yield return t;
                continue;
            }
            var child = node.Children.First(c => c.AIMName == to.AimName);
            if (child.IsComposite)
                foreach (var t in Sinks(child, new Endpoint(null, to.DataType, to.PortNumber))) yield return t;
            else
            {
                var port = OwnPort(child, "Input", to.DataType, to.PortNumber);
                yield return new Terminal(child.AIMName, port?.DataType ?? to.DataType, port is null ? to.PortNumber : NumberOf(child, port));
            }
        }
    }

    private static RuntimePort? OwnPort(DescriptorNode aim, string direction, string dataType, int number)
    {
        var candidates = aim.Ports.Where(p => p.Direction == direction && p.Accepts(dataType)).ToList();
        return candidates.FirstOrDefault(p => NumberOf(aim, p) == number) ?? (candidates.Count == 1 ? candidates[0] : null);
    }

    // What an AIM's Message carries a Port's datum under: the Port's Data Type and
    // Number (PortKey), never its name.
    private static string KeyOf(DescriptorNode aim, RuntimePort port) => PortKey.Of(port.DataType, NumberOf(aim, port));

    private static int NumberOf(DescriptorNode aim, RuntimePort port) =>
        port.PortNumber ??
        aim.Ports.Where(p => p.Direction == port.Direction && p.DataType == port.DataType).ToList().IndexOf(port) + 1;

    private static PortBehaviour BehaviourOf(RuntimePort? port) => port is null
        ? PortBehaviour.Default
        : new PortBehaviour(
            port.Depth ?? PortBehaviour.DefaultDepth,
            port.Overflow switch { "DropOldest" => Overflow.DropOldest, "DropNewest" => Overflow.DropNewest, _ => Overflow.Block },
            port.MaxAge is { } ms ? TimeSpan.FromMilliseconds(ms) : null);

    // ---- Start and Stop ------------------------------------------------------

    public void Start()
    {
        // A Message a reader loses releases, for that reader, what it references.
        foreach (var transport in transports.Values.OfType<ChannelTransport>())
        {
            var before = transport.Lost;
            transport.Lost = (spec, message) =>
            {
                before?.Invoke(spec, message);
                if (spec.Module == module) foreach (var reference in message.Payloads) Payloads.Release(reference);
            };
        }

        // A Message leaving for another machine carries its payloads inline.
        foreach (var remote in transports.Values.OfType<RemoteTransport>())
        {
            var before = remote.Leaving;
            remote.Leaving = (spec, json) => spec.Module == module ? Payloads.Inline(json) : before?.Invoke(spec, json) ?? json;
        }

        // The ends on this machine: the writers and readers of the AIMs that run
        // here, and the boundary's, which is the Controller's.
        bool Here(PortEnd end) => runsHere(end.Aim);
        foreach (var spec in channels)
        {
            var transport = transports[spec.Transport];
            (transport as RemoteTransport)?.Register(spec);
            if (Here(spec.Writer)) Add(writers, spec.Writer, transport.OpenWriter(spec));
            foreach (var reader in spec.Readers.Where(r => Here(r.Reader)))
                Add(readers, reader.Reader, transport.OpenReader(spec, reader.Reader));
        }

        foreach (var leaf in leaves.Values.Where(l => runsHere(l.AIMName)))
            running.Add(Task.Run(() => RunAimAsync(leaf)));
    }

    private static void Add<T>(Dictionary<PortEnd, List<T>> table, PortEnd end, T item)
    {
        if (!table.TryGetValue(end, out var list)) table[end] = list = new List<T>();
        list.Add(item);
    }

    public async Task StopAsync()
    {
        host.StopModule();
        foreach (var transport in transports.Values.OfType<ChannelTransport>()) transport.Close(module);
        await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { });
    }

    // ---- the boundary (M3215 3.3) ------------------------------------------------

    public async ValueTask<AifError> WriteBoundaryAsync(string dataType, int portNumber, string json, int timeoutMs,
                                                        string? from = null, DateTimeOffset? fromStamp = null)
    {
        if (!writers.TryGetValue(new PortEnd("", dataType, portNumber), out var ends)) return AifError.NoSuchPort;
        var all = true;
        var first = true;
        foreach (var end in ends)
        {
            // Recorded once, as the first of its Channels stamps it.
            var written = new PortMessage { DataType = dataType, PortNumber = portNumber, Json = json, Payloads = PayloadStore.ReferencesIn(json), RecordAsInput = first,
                                            From = from, FromStamp = fromStamp };
            first = false;
            all &= await end.WriteAsync(written, timeoutMs);
        }
        return all ? AifError.OK : AifError.Timeout;
    }

    public async ValueTask<(AifError, string?)> ReadBoundaryAsync(string dataType, int portNumber, int timeoutMs)
    {
        var (error, json, _) = await ReadBoundaryStampedAsync(dataType, portNumber, timeoutMs);
        return (error, json);
    }

    // With its stamp: what an External Output Port gives another Controller carries it.
    public async ValueTask<(AifError, string?, DateTimeOffset)> ReadBoundaryStampedAsync(string dataType, int portNumber, int timeoutMs)
    {
        if (!readers.TryGetValue(new PortEnd("", dataType, portNumber), out var ends)) return (AifError.NoSuchPort, null, default);
        var message = await ReadAnyAsync(ends, timeoutMs, CancellationToken.None);
        return message is null ? (AifError.NotProduced, null, default) : (AifError.OK, Payloads.Inline(message.Json), message.Stamp);
    }

    // MPAI_AIFU_Payload_Put: the User Agent places a payload for a boundary Input
    // Port, and writes its Object with the reference returned.
    public (AifError, string?) PutBoundaryPayload(string dataType, int portNumber, ReadOnlyMemory<byte> data)
    {
        var spec = channels.FirstOrDefault(c => c.Writer == new PortEnd("", dataType, portNumber));
        return spec is null ? (AifError.NoSuchPort, null) : (AifError.OK, Payloads.Put(spec, data));
    }

    // What every Channel carried: written, and at each reader taken, dropped,
    // discarded, pending. An end on another machine is accounted for there.
    public IReadOnlyList<string> Accounts() =>
        channels.Select(spec =>
        {
            var written = writers.TryGetValue(spec.Writer, out var w)
                ? w.Where(x => x.Spec.Id == spec.Id).Select(x => x.Written).DefaultIfEmpty().Max().ToString()
                : "elsewhere";
            var ends = spec.Readers.Select(r =>
            {
                var end = readers.GetValueOrDefault(r.Reader)?.FirstOrDefault(x => x.Spec.Id == spec.Id);
                return end is null
                    ? $"{r.Reader}: elsewhere"
                    : $"{r.Reader}: taken {end.Taken}, dropped {end.Dropped}, discarded {end.Discarded}, pending {end.Pending}";
            });
            return $"{spec.Writer} ({spec.Transport}) wrote {written}; " + string.Join("; ", ends);
        }).ToList();

    // ---- one AIM -------------------------------------------------------------

    private async Task RunAimAsync(DescriptorNode leaf)
    {
        var aim = leaf.AIMName;
        var ports = new Ports(this, leaf);
        while (!host.IsStopped && !host.IsDead(aim))
        {
            try
            {
                if (host.Processor(aim) is IAimRunner runner)
                {
                    var context = await host.ContextAsync(aim);
                    using var watching = CancellationTokenSource.CreateLinkedTokenSource(host.Stopping);
                    var watch = WatchMaxAgeAsync(leaf, ports, watching.Token);
                    try { await runner.RunAsync(ports, context); }
                    finally
                    {
                        watching.Cancel();
                        try { await watch; } catch (OperationCanceledException) { }
                    }
                    return;                                             // it ran until Stop
                }
                if (!await FireOnceAsync(leaf, ports)) return;
                lock (misses)
                    if (misses.GetValueOrDefault(aim).InARow > 0) continue;   // a run past its Deadline is not a clean one
                host.Succeeded(aim);
            }
            catch (OperationCanceledException) when (host.IsStopped || host.IsDead(aim))
            {
                return;
            }
            catch (Exception failure)
            {
                if (!Failed(leaf, $"it threw: {failure.Message}")) return;
            }
        }
    }

    // RestartLimit, then the policy (M3215 3.3): an AIM that fails is DEGRADED and
    // started again at most RestartLimit times; beyond them the composite's
    // OnDegraded applies. True while the AIM is to go on.
    private bool Failed(DescriptorNode leaf, string reason)
    {
        var aim = leaf.AIMName;
        lock (restarts)
        {
            var n = restarts.GetValueOrDefault(aim);
            if (n < leaf.RestartLimit)
            {
                restarts[aim] = n + 1;
                host.Degrade(aim, $"{reason}; started again, {n + 1} of {leaf.RestartLimit}");
                return true;
            }
        }
        return Degraded(leaf, reason);
    }

    // The AIM is DEGRADED - it failed beyond its restarts, missed its Deadline three
    // times running, or a required Port delivered nothing within its MaxAge
    // (M3205 5.4) - and the composite's policy applies. True while it is to go on.
    private bool Degraded(DescriptorNode leaf, string reason)
    {
        var aim = leaf.AIMName;
        host.Degrade(aim, reason);
        Console.WriteLine($"[AIF] {aim}: DEGRADED ({reason}); {parent[leaf].AIMName} OnDegraded {parent[leaf].OnDegraded}");
        OnDegradedNotice?.Invoke(aim, reason, parent[leaf].OnDegraded);
        switch (parent[leaf].OnDegraded)
        {
            case "Continue":
                return true;
            case "StopAIM":
                host.StopAim(aim, $"{reason}; OnDegraded StopAIM");
                return false;
            default:
                _ = StopAsync();
                return false;
        }
    }

    // AN AIM THAT RUNS ON ITS OWN PORTS reads them itself, so the Controller watches
    // beside it that each required Port with a MaxAge goes on delivering (M3215 3.2,
    // M3221 3.7): once a Port has delivered, a silence longer than its MaxAge makes
    // the AIM DEGRADED and its Module's OnDegraded applies; a new arrival makes it
    // ALIVE again. A Port that has not yet delivered is a sensor not yet started,
    // not one that stopped.
    private async Task WatchMaxAgeAsync(DescriptorNode leaf, Ports ports, CancellationToken stop)
    {
        var watched = leaf.Ports.Where(p => p.Direction == "Input" && !p.IsOptional && p.MaxAge is not null)
                                .Select(p => (Port: p, Ends: ports.ReadersOf(p)))
                                .Where(x => x.Ends.Count > 0)
                                .ToList();
        if (watched.Count == 0) return;
        static long Arrived(IReadOnlyList<IChannelReader> ends) => ends.Sum(e => e.Taken + e.Pending + e.Dropped + e.Discarded);
        var last = watched.ToDictionary(x => x.Port, x => (Count: Arrived(x.Ends), At: System.Diagnostics.Stopwatch.GetTimestamp()));
        var every = TimeSpan.FromMilliseconds(Math.Max(10, watched.Min(x => x.Port.MaxAge!.Value) / 4));
        var silent = false;
        while (true)
        {
            await Task.Delay(every, stop);
            foreach (var (port, ends) in watched)
            {
                var now = Arrived(ends);
                if (now != last[port].Count)
                {
                    last[port] = (now, System.Diagnostics.Stopwatch.GetTimestamp());
                    if (silent) { silent = false; host.Succeeded(leaf.AIMName); }
                    continue;
                }
                if (now == 0 || silent || System.Diagnostics.Stopwatch.GetElapsedTime(last[port].At).TotalMilliseconds <= port.MaxAge!.Value) continue;
                silent = true;
                if (!Degraded(leaf, $"its Port {port.DataType}#{NumberOf(leaf, port)} delivered nothing within its MaxAge of {port.MaxAge} ms")) return;
            }
        }
    }

    // The adapter: waits until the AIM may fire, fires it once, writes what it
    // returns. False when the Module is stopped.
    private async Task<bool> FireOnceAsync(DescriptorNode leaf, Ports ports)
    {
        var inputs = leaf.Ports.Where(p => p.Direction == "Input")
                                .Select(p => (Port: p, Ends: ports.ReadersOf(p)))
                                .Where(x => x.Ends.Count > 0)
                                .ToList();
        var inbox = new Dictionary<string, string>();

        if (leaf.Period is not null)
        {
            // AN AIM WITH A PERIOD is fired at each Period, on the latest Message of
            // each input, whatever has or has not arrived.
            await ports.NextPeriodAsync();
            host.Stopping.ThrowIfCancellationRequested();
            foreach (var (port, ends) in inputs)
                foreach (var end in ends)
                    while (end.TryRead(out var message) && message is not null) inbox[KeyOf(leaf, port)] = Payloads.Inline(message.Json);
        }
        else
        {
            if (inputs.Count == 0)
            {
                await Task.Delay(Timeout.Infinite, host.Stopping);          // nothing will ever arrive
                return false;
            }

            var required = inputs.Where(x => !x.Port.IsOptional).ToList();
            while (true)
            {
                host.Stopping.ThrowIfCancellationRequested();
                var emptyRequired = required.Where(x => x.Ends.All(e => e.Pending == 0)).ToList();
                if (emptyRequired.Count == 0 && inputs.Any(x => x.Ends.Any(e => e.Pending > 0))) break;
                var waitOn = emptyRequired.Count > 0 ? emptyRequired : inputs;

                // A REQUIRED PORT THAT DELIVERS NOTHING WITHIN ITS MAXAGE makes its
                // AIM DEGRADED (M3215 3.2), once until something arrives.
                var maxAge = emptyRequired.Where(x => x.Port.MaxAge is not null).Select(x => x.Port.MaxAge!.Value).DefaultIfEmpty(-1).Min();
                var arrival = Task.WhenAny(waitOn.SelectMany(x => x.Ends).Select(e => e.WaitAsync(host.Stopping)));
                if (maxAge < 0 || starved.Contains(leaf.AIMName)) { await arrival; continue; }
                if (await Task.WhenAny(arrival, Task.Delay(TimeSpan.FromMilliseconds(maxAge), host.Stopping)) == arrival) continue;
                var late = emptyRequired.First(x => x.Port.MaxAge is not null).Port;
                lock (starved) starved.Add(leaf.AIMName);
                if (!Degraded(leaf, $"its Port {late.DataType}#{NumberOf(leaf, late)} delivered nothing within its MaxAge of {late.MaxAge} ms"))
                    return false;
            }
            lock (starved) starved.Remove(leaf.AIMName);

            foreach (var (port, ends) in inputs)
                foreach (var end in ends)
                    if (end.TryRead(out var message) && message is not null) { inbox[KeyOf(leaf, port)] = Payloads.Inline(message.Json); break; }
        }

        var clockAtStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = await host.ProcessAsync(leaf.AIMName, new Message
        {
            MessageId = Guid.NewGuid().ToString(), MessageType = module, Ports = inbox
        });
        if (leaf.Deadline is { } deadline && !Kept(leaf, deadline, System.Diagnostics.Stopwatch.GetElapsedTime(clockAtStart)))
            return false;

        if (result.IsCancelled) throw new OperationCanceledException(result.Payload);
        if (result.IsError) throw new InvalidOperationException($"it returned an error: {result.Payload}");

        foreach (var (name, json) in result.Ports)
        {
            var port = leaf.Ports.FirstOrDefault(p => p.Direction == "Output" && KeyOf(leaf, p) == name);
            if (port is null) { Console.WriteLine($"[AIF] {leaf.AIMName}: produced '{name}', which is not one of its Output Ports; dropped"); continue; }
            await ports.WriteAsync(port.DataType, NumberOf(leaf, port), json, -1);
        }
        return true;
    }

    // The AIMs waiting on a required Port past its MaxAge, already DEGRADED for it.
    private readonly HashSet<string> starved = new();

    // A run against the AIM's Deadline: a miss is counted; three in a row make the
    // AIM DEGRADED and its composite's policy applies (M3215 3.5). False when the
    // AIM is not to go on.
    private bool Kept(DescriptorNode leaf, double deadline, TimeSpan took)
    {
        var aim = leaf.AIMName;
        int inARow;
        lock (misses)
        {
            var (all, row) = misses.GetValueOrDefault(aim);
            if (took.TotalMilliseconds <= deadline) { misses[aim] = (all, 0); return true; }
            misses[aim] = (all + 1, row + 1);
            inARow = row + 1;
        }
        Console.WriteLine($"[AIF] {aim}: missed its Deadline of {deadline} ms ({took.TotalMilliseconds:0} ms)");
        return inARow < 3 || Degraded(leaf, $"missed its Deadline of {deadline} ms {inARow} times running");
    }

    private static async ValueTask<PortMessage?> ReadAnyAsync(IReadOnlyList<IChannelReader> ends, int timeoutMs, CancellationToken cancel)
    {
        foreach (var end in ends)
            if (end.TryRead(out var ready) && ready is not null) return ready;
        if (timeoutMs == 0) return null;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        if (timeoutMs > 0) limit.CancelAfter(timeoutMs);
        try
        {
            while (true)
            {
                await Task.WhenAny(ends.Select(e => e.WaitAsync(limit.Token)));
                limit.Token.ThrowIfCancellationRequested();
                foreach (var end in ends)
                    if (end.TryRead(out var message) && message is not null) return message;
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { return null; }
    }

    // ---- an AIM's Ports ------------------------------------------------------------

    private sealed class Ports : IAimPorts
    {
        private readonly ContinuousExecutor executor;
        private readonly DescriptorNode leaf;
        private readonly PeriodicTimer? period;

        public Ports(ContinuousExecutor executor, DescriptorNode leaf)
        {
            this.executor = executor;
            this.leaf = leaf;
            if (leaf.Period is { } ms) period = new PeriodicTimer(TimeSpan.FromMilliseconds(ms));
        }

        public async Task NextPeriodAsync()
        {
            if (period is null) return;
            await period.WaitForNextTickAsync(executor.host.Stopping);
        }

        private PortEnd End(string direction, string dataType, int portNumber)
        {
            var port = OwnPort(leaf, direction, dataType, portNumber)
                ?? throw new InvalidOperationException($"{leaf.AIMName} has no {direction} Port {dataType}#{portNumber}.");
            return new PortEnd(leaf.AIMName, port.DataType, NumberOf(leaf, port));
        }

        public IReadOnlyList<IChannelReader> ReadersOf(RuntimePort port) =>
            executor.readers.TryGetValue(new PortEnd(leaf.AIMName, port.DataType, NumberOf(leaf, port)), out var ends)
                ? ends : Array.Empty<IChannelReader>();

        private IReadOnlyList<IChannelReader> Readers(string dataType, int portNumber) =>
            executor.readers.TryGetValue(End("Input", dataType, portNumber), out var ends) ? ends : Array.Empty<IChannelReader>();

        public async ValueTask<PortMessage?> ReadAsync(string dataType, int portNumber = 1, int timeoutMs = -1)
        {
            await executor.host.WhilePausedAsync();
            return await ReadAnyAsync(Readers(dataType, portNumber), timeoutMs, executor.host.Stopping);
        }

        public async ValueTask<bool> WriteAsync(string dataType, int portNumber, string json, int timeoutMs = -1)
        {
            var end = End("Output", dataType, portNumber);
            if (!executor.writers.TryGetValue(end, out var writers)) return true;   // connected to nothing
            var all = true;
            foreach (var writer in writers)
                all &= await writer.WriteAsync(new PortMessage
                {
                    DataType = end.DataType, PortNumber = end.PortNumber, Json = json, Payloads = PayloadStore.ReferencesIn(json)
                }, timeoutMs, executor.host.Stopping);
            return all;
        }

        public async ValueTask<(string DataType, int PortNumber)?> SelectAsync(int timeoutMs, params (string DataType, int PortNumber)[] inputs)
        {
            var ends = inputs.Select(i => (Input: i, Readers: Readers(i.DataType, i.PortNumber))).ToList();
            (string, int)? Ready() => ends.FirstOrDefault(e => e.Readers.Any(r => r.Pending > 0)) is { Readers.Count: > 0 } found ? found.Input : null;
            if (Ready() is { } now) return now;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(executor.host.Stopping);
            if (timeoutMs >= 0) limit.CancelAfter(timeoutMs);
            try
            {
                while (true)
                {
                    await Task.WhenAny(ends.SelectMany(e => e.Readers).Select(r => r.WaitAsync(limit.Token)).Append(Task.Delay(Timeout.Infinite, limit.Token)));
                    limit.Token.ThrowIfCancellationRequested();
                    if (Ready() is { } ready) return ready;
                }
            }
            catch (OperationCanceledException) when (!executor.host.IsStopped) { return null; }
        }

        public int Pending(string dataType, int portNumber = 1) => Readers(dataType, portNumber).Sum(r => r.Pending);

        public long Dropped(string dataType, int portNumber = 1) => Readers(dataType, portNumber).Sum(r => r.Dropped + r.Discarded);

        public DateTimeOffset Now => executor.clock.Now;

        public string PutPayload(string dataType, int portNumber, ReadOnlyMemory<byte> data)
        {
            var end = End("Output", dataType, portNumber);
            var spec = executor.channels.FirstOrDefault(c => c.Writer == end)
                ?? throw new InvalidOperationException($"{end} writes no Channel.");
            return executor.Payloads.Put(spec, data);
        }

        public ReadOnlyMemory<byte> GetPayload(string reference) =>
            executor.Payloads.TryGet(reference, out var data)
                ? data
                : throw new KeyNotFoundException($"{reference} is not held: released, past its MaxAge, or not issued by this Controller.");

        public void ReleasePayload(string reference) => executor.Payloads.Release(reference);
    }
}
