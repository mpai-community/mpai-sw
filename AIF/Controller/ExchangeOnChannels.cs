using AIF.Channels;

namespace AIF.Controller;

// THE EXCHANGE, ON THE CONTINUOUS EXECUTOR (M3205 5.1, M3215 3.4). The Messages
// written to the boundary; each AIM fired at most once, on its Channels; the
// outputs read. OI-12, as settled with the author:
// - an AIM is ready when each input it is connected to has either received its
//   Message of this exchange or can no longer receive one, because every AIM that
//   could still produce it has fired or will not fire; it fires when it is ready
//   and has received at least one Message, and is skipped when it has received
//   none;
// - the exchange is finished for what the User Agent asks: a boundary Output Port
//   is settled as soon as it has received its Message of this exchange, or can no
//   longer receive one.
// A Module with a loop cannot run as an exchange: its AIMs would wait for each
// other. Its Metadata declares Execution: Continuous.
public sealed partial class ContinuousExecutor
{
    private readonly SemaphoreSlim oneExchange = new(1, 1);
    private bool opened;

    // Who can write what an input end reads: the writers of the Channels it is a
    // reader of. A boundary writer has written all it will at the start.
    private Dictionary<PortEnd, List<PortEnd>>? producers;

    private void OpenForExchanges()
    {
        if (opened) return;
        opened = true;
        foreach (var spec in channels)
        {
            var transport = transports[spec.Transport];
            Add(writers, spec.Writer, transport.OpenWriter(spec));
            foreach (var reader in spec.Readers)
                Add(readers, reader.Reader, transport.OpenReader(spec, reader.Reader));
        }

        // Where a destination is fed both by the boundary and by an AIM, the
        // boundary's value wins when present: its reader end is read first.
        foreach (var ends in readers.Values)
            ends.Sort((a, b) => b.Spec.Writer.IsBoundary.CompareTo(a.Spec.Writer.IsBoundary));

        producers = channels.SelectMany(c => c.Readers.Select(r => (r.Reader, c.Writer)))
                            .GroupBy(x => x.Reader)
                            .ToDictionary(g => g.Key, g => g.Select(x => x.Writer).ToList());
    }

    public ExchangeRun Exchange(IReadOnlyDictionary<string, string> boundary, string messageId)
    {
        var run = new ExchangeRun(this);
        run.Completed = RunExchangeAsync(run, boundary, messageId);
        return run;
    }

    private async Task<Message> RunExchangeAsync(ExchangeRun run, IReadOnlyDictionary<string, string> boundary, string messageId)
    {
        await oneExchange.WaitAsync();
        try
        {
            return graph.Root.IsComposite
                ? await ExchangeAsync(run, boundary, messageId)
                : await AimOnItsOwnAsync(graph.Root, boundary, messageId);
        }
        catch
        {
            run.Settle(this, _ => true);                    // nothing more will come: no reader waits in vain
            throw;
        }
        finally { oneExchange.Release(); }
    }

    private async Task<Message> ExchangeAsync(ExchangeRun run, IReadOnlyDictionary<string, string> boundary, string messageId)
    {
        OpenForExchanges();

        // Each exchange starts clean: nothing from the last one is left at any
        // reader.
        foreach (var ends in readers.Values)
            foreach (var end in ends)
                while (end.TryRead(out var stale) && stale is not null)
                    foreach (var reference in stale.Payloads) Payloads.Release(reference);

        // The boundary written. A datum for a Port the Module does not
        // declare meets no Channel, as before.
        foreach (var (key, json) in boundary)
        {
            var hash = key.LastIndexOf('#');
            var dataType = hash > 0 ? key[..hash] : key;
            var number = hash > 0 && int.TryParse(key[(hash + 1)..], out var n) ? n : 1;
            await WriteBoundaryAsync(dataType, number, json, -1);
        }

        var done = new HashSet<string>();
        var stopped = false;
        bool Done(PortEnd writer) => writer.IsBoundary || done.Contains(writer.Aim);

        bool Settled(PortEnd reader) =>
            readers.TryGetValue(reader, out var ends) && ends.Any(e => e.Pending > 0) ||
            producers!.GetValueOrDefault(reader, new List<PortEnd>()).All(Done);

        var order = leaves.Values.ToList();
        var progress = true;
        while (progress && !stopped)
        {
            progress = false;
            foreach (var leaf in order)
            {
                if (done.Contains(leaf.AIMName)) continue;
                if (host.IsStopped) { stopped = true; break; }

                var inputs = InputEnds(leaf);
                if (!inputs.All(i => Settled(i.End))) continue;

                done.Add(leaf.AIMName);
                progress = true;
                if (host.IsDead(leaf.AIMName) || !inputs.Any(i => readers[i.End].Any(e => e.Pending > 0)))
                {
                    run.Settle(this, Done);
                    continue;                                           // stopped, or nothing to work on: skipped
                }

                if (!await FireInExchangeAsync(run, leaf, inputs, messageId)) stopped = true;
                run.Settle(this, Done);
                if (stopped) break;
            }
        }

        // Every AIM fired or skipped, or the Module stopped: every Port settles.
        foreach (var leaf in order) done.Add(leaf.AIMName);
        run.Settle(this, _ => true);

        return new Message
        {
            MessageId   = messageId,
            MessageType = run.Cancelled ? Message.CancelledType : module,
            Ports       = stopped ? new Dictionary<string, string>() : run.Outputs()
        };
    }

    // WHAT WAS STARTED MAY BE A BASIC AIM. A Remote Client may start any AIM
    // (MPAI-MAS action 6), and a Service that offers one AIM runs that AIM. Nothing
    // contains it: its own Ports are the boundary, so there is no Channel to run
    // on. What the boundary carries, keyed by Data Type and Port Number, is given
    // to it by its own Port names, and its outputs are put back on the boundary.
    private async Task<Message> AimOnItsOwnAsync(DescriptorNode aim, IReadOnlyDictionary<string, string> boundary, string messageId)
    {
        host.BeginRun();
        var inbox = new Dictionary<string, string>();
        foreach (var (key, json) in boundary)
        {
            var hash = key.LastIndexOf('#');
            var dataType = hash > 0 ? key[..hash] : key;
            var number = hash > 0 && int.TryParse(key[(hash + 1)..], out var n) ? n : 1;
            if (OwnPort(aim, "Input", dataType, number) is { } port) inbox[KeyOf(aim, port)] = json;
        }

        Message result;
        try
        {
            result = await host.ProcessAsync(aim.AIMName, new Message { MessageId = messageId, MessageType = module, Ports = inbox });
        }
        catch (OperationCanceledException cancelled)
        {
            return Message.Cancelled(messageId, aim.AIMName, cancelled.Message);
        }
        catch (Exception failure)
        {
            // Nothing contains it, so it has no policy: it is DEGRADED, and what to
            // do is the User Agent's decision.
            host.Degrade(aim.AIMName, $"it threw: {failure.Message}");
            Console.WriteLine($"[AIF] {aim.AIMName}: threw, produced no output: {failure.Message}");
            return new Message { MessageId = messageId, MessageType = module };
        }

        if (result.IsCancelled) return result;
        if (result.IsError)
        {
            host.Degrade(aim.AIMName, $"it returned an error: {result.Payload}");
            Console.WriteLine($"[AIF] {aim.AIMName}: error, produced no output: {result.Payload}");
            return new Message { MessageId = messageId, MessageType = module };
        }
        host.Succeeded(aim.AIMName);

        var produced = new Dictionary<string, string>();
        foreach (var (name, json) in result.Ports)
        {
            if (aim.Ports.FirstOrDefault(p => p.Direction == "Output" && KeyOf(aim, p) == name) is not { } port) continue;
            ObjectInspector?.Invoke(aim.AIMName, port.DataType, json);
            produced[$"{port.DataType}#{NumberOf(aim, port)}"] = json;
        }
        return new Message { MessageId = messageId, MessageType = module, Ports = produced };
    }

    private List<(RuntimePort Port, PortEnd End)> InputEnds(DescriptorNode leaf) =>
        leaf.Ports.Where(p => p.Direction == "Input")
                  .Select(p => (Port: p, End: new PortEnd(leaf.AIMName, p.DataType, NumberOf(leaf, p))))
                  .Where(x => readers.ContainsKey(x.End))
                  .ToList();

    // One AIM of the exchange: fired on what it received, its outputs written; a
    // failure is DEGRADED and the composite's policy applies (Phase 3). False when
    // the Module is stopped.
    private async Task<bool> FireInExchangeAsync(ExchangeRun run, DescriptorNode leaf, List<(RuntimePort Port, PortEnd End)> inputs, string messageId)
    {
        var inbox = new Dictionary<string, string>();
        foreach (var (port, end) in inputs)
            foreach (var reader in readers[end])
                if (reader.TryRead(out var message) && message is not null) { inbox[KeyOf(leaf, port)] = Payloads.Inline(message.Json); break; }

        Message result;
        try
        {
            result = await host.ProcessAsync(leaf.AIMName, new Message { MessageId = messageId, MessageType = module, Ports = inbox });
        }
        catch (OperationCanceledException)
        {
            if (!host.IsStopped) return true;                        // one AIM stopped: skipped
            run.Cancelled = true;                                    // the Module stopped: the run is cancelled
            return false;
        }
        catch (Exception failure)
        {
            return Degraded(leaf, $"it threw: {failure.Message}") || !host.IsStopped;
        }

        if (result.IsCancelled)
        {
            if (!host.IsStopped) return true;
            run.Cancelled = true;
            return false;
        }
        if (result.IsError) return Degraded(leaf, $"it returned an error: {result.Payload}") || !host.IsStopped;

        host.Succeeded(leaf.AIMName);
        var ports = new Ports(this, leaf);
        foreach (var (name, json) in result.Ports)
        {
            var port = leaf.Ports.FirstOrDefault(p => p.Direction == "Output" && KeyOf(leaf, p) == name);
            if (port is null) { Console.WriteLine($"[AIF] {leaf.AIMName}: produced '{name}', which is not one of its Output Ports; dropped"); continue; }
            await ports.WriteAsync(port.DataType, NumberOf(leaf, port), json, -1);
        }
        return true;
    }

    // One exchange as the User Agent sees it: each boundary Output Port settles as
    // soon as it has its Message or can no longer get one; Completed when every
    // AIM has fired or been skipped.
    public sealed class ExchangeRun
    {
        private readonly Dictionary<PortEnd, TaskCompletionSource<string?>> ports = new();
        private readonly Dictionary<PortEnd, string> outputs = new();

        internal ExchangeRun(ContinuousExecutor executor)
        {
            foreach (var spec in executor.channels)
                foreach (var reader in spec.Readers.Where(r => r.Reader.IsBoundary))
                    ports.TryAdd(reader.Reader, new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        public Task<Message> Completed { get; internal set; } = null!;

        internal bool Cancelled { get; set; }

        // The datum of a boundary Output Port in this exchange, or null when it
        // produced nothing; null too for a Port no Channel reaches.
        public Task<string?> Port(string dataType, int portNumber) =>
            ports.TryGetValue(new PortEnd("", dataType, portNumber), out var port)
                ? port.Task
                : Completed.ContinueWith(done => done.IsCompletedSuccessfully &&
                                                 done.Result.Ports.TryGetValue($"{dataType}#{portNumber}", out var json) ? json : null);

        internal void Settle(ContinuousExecutor executor, Func<PortEnd, bool> done)
        {
            foreach (var (end, port) in ports)
            {
                if (port.Task.IsCompleted) continue;
                foreach (var reader in executor.readers[end])
                    if (reader.TryRead(out var message) && message is not null)
                    {
                        var json = executor.Payloads.Inline(message.Json);
                        outputs[end] = json;
                        port.TrySetResult(json);
                        break;
                    }
                if (!port.Task.IsCompleted && executor.producers!.GetValueOrDefault(end, new List<PortEnd>()).All(done))
                    port.TrySetResult(null);
            }
        }

        internal Dictionary<string, string> Outputs() =>
            outputs.ToDictionary(o => $"{o.Key.DataType}#{o.Key.PortNumber}", o => o.Value);
    }
}
