using System.Collections.Concurrent;

namespace AIF.Channels;

// Delivers a Message to a reader end on another machine; false when it could not
// be put there within the timeout.
public delegate ValueTask<bool> RemoteDelivery(ChannelSpec spec, PortEnd reader, PortMessage message, int timeoutMs, CancellationToken cancel);

// What every transport shares: one Channel's reader ends on this machine, the
// deliveries to its reader ends on others, and the writer's stamp.
internal sealed class ChannelCore
{
    public ChannelSpec Spec { get; }
    public IReadOnlyDictionary<PortEnd, ReaderQueue> Readers { get; }
    private readonly IReadOnlyList<PortEnd> elsewhere;
    private readonly RemoteDelivery? remote;
    private readonly Action<ChannelSpec, PortEnd, PortMessage>? delivered;
    private readonly IClock clock;
    private readonly Func<Action<ChannelSpec, PortMessage>?> stamped;
    private long sequence;

    public long Written => Interlocked.Read(ref sequence);

    public ChannelCore(ChannelSpec spec, IClock clock, Action<ChannelSpec, PortMessage>? lost,
                       Func<PortEnd, bool>? isHere = null, RemoteDelivery? remote = null,
                       Action<ChannelSpec, PortEnd, PortMessage>? delivered = null,
                       Func<Action<ChannelSpec, PortMessage>?>? stamped = null)
    {
        Spec = spec;
        this.clock = clock;
        this.remote = remote;
        this.delivered = delivered;
        this.stamped = stamped ?? (() => null);
        var here = isHere ?? (_ => true);
        Readers = spec.Readers.Where(r => here(r.Reader)).ToDictionary(r => r.Reader, r => new ReaderQueue(r.Behaviour, clock,
            lost is null ? null : message => lost(spec, message)));
        elsewhere = spec.Readers.Where(r => !here(r.Reader)).Select(r => r.Reader).ToList();
    }

    // The writer's end stamps the Message; the writer supplies no stamp. Where
    // something observes the stamping - the record of a boundary - the stamp and the
    // observation are one step, under one lock for every Channel: whoever observes
    // sees Messages in the order of their stamps. Taken apart, a Message stamped
    // earlier on one thread could be observed after one stamped later on another,
    // and a record's sequence went back in time.
    public void Stamp(PortMessage message)
    {
        if (stamped() is { } observe)
            lock (StampOrder.Gate)
            {
                StampHere(message);
                observe(Spec, message);
            }
        else StampHere(message);
    }

    private void StampHere(PortMessage message)
    {
        message.Stamp    = clock.Now;
        message.Written  = clock.Monotonic;
        message.Sequence = Interlocked.Increment(ref sequence);
    }

    public async ValueTask<bool> DeliverAsync(PortMessage message, int timeoutMs, CancellationToken cancel)
    {
        var all = true;
        foreach (var (end, reader) in Readers)
        {
            var put = await reader.PutAsync(message, timeoutMs, cancel);
            if (put) delivered?.Invoke(Spec, end, message);
            all &= put;
        }
        foreach (var reader in elsewhere)
            all &= await remote!(Spec, reader, message, timeoutMs, cancel);
        return all;
    }

    // A Message that came from the writer's machine, for a reader here: stamped
    // there; its age, for MaxAge, counted from its arrival here, since two
    // machines' monotonic clocks are not comparable.
    public ValueTask<bool> ArrivedAsync(PortEnd reader, PortMessage message, int timeoutMs, CancellationToken cancel)
    {
        message.Written = clock.Monotonic;
        return Readers.TryGetValue(reader, out var queue) ? Put(queue) : ValueTask.FromResult(false);

        async ValueTask<bool> Put(ReaderQueue queue)
        {
            var put = await queue.PutAsync(message, timeoutMs, cancel);
            if (put) delivered?.Invoke(Spec, reader, message);
            return put;
        }
    }

    public void Close()
    {
        foreach (var reader in Readers.Values) reader.Close();
    }
}

// A reader end, whichever transport serves its Channel.
internal sealed class ReaderEnd : IChannelReader
{
    private readonly ReaderQueue queue;

    public ReaderEnd(ChannelSpec spec, PortEnd reader, ReaderQueue queue)
    {
        Spec = spec;
        Reader = reader;
        this.queue = queue;
    }

    public ChannelSpec Spec { get; }
    public PortEnd Reader { get; }
    public ValueTask<PortMessage?> ReadAsync(int timeoutMs = -1, CancellationToken cancel = default) => queue.TakeAsync(timeoutMs, cancel);
    public bool TryRead(out PortMessage? message) => queue.TryTake(out message);
    public Task WaitAsync(CancellationToken cancel = default) => queue.Arrival().WaitAsync(cancel);
    public int  Pending   => queue.Pending;
    public long Taken     => Interlocked.Read(ref queue.Taken);
    public long Dropped   => Interlocked.Read(ref queue.Dropped);
    public long Discarded => Interlocked.Read(ref queue.Discarded);
}

// Channels opened through one transport, by identity: the writer's end and the
// readers' ends of one Channel meet here.
public abstract class ChannelTransport : IChannelTransport
{
    private readonly ConcurrentDictionary<string, ChannelCore> channels = new();
    protected IClock Clock { get; }

    protected ChannelTransport(IClock? clock) => Clock = clock ?? SystemClock.Instance;

    public abstract string Name { get; }

    internal ChannelCore Core(ChannelSpec spec) => channels.GetOrAdd(spec.Id, _ => new ChannelCore(spec, Clock, (s, m) => Lost?.Invoke(s, m), r => IsHere(spec, r), Remote,
                                                                                                   (s, r, m) => Delivered?.Invoke(s, r, m), () => Stamped));

    internal ChannelCore? Core(string channelId) => channels.TryGetValue(channelId, out var core) ? core : null;

    // Where a reader end is: here, unless a transport that reaches other
    // machines says otherwise; and how a Message is delivered to one elsewhere.
    protected virtual bool IsHere(ChannelSpec spec, PortEnd reader) => true;
    protected virtual RemoteDelivery? Remote => null;

    // Called with every Message a reader end loses, dropped or discarded.
    public Action<ChannelSpec, PortMessage>? Lost { get; set; }

    // Called with every Message put into a reader end on this machine: the place
    // to see what reaches a Module's boundary, whatever carried it (M3219 3.4).
    public Action<ChannelSpec, PortEnd, PortMessage>? Delivered { get; set; }

    // Called with every Message stamped on this machine, as it is stamped and under
    // the same lock (StampOrder): the place to record in the order of the stamps.
    public Action<ChannelSpec, PortMessage>? Stamped { get; set; }

    public abstract IChannelWriter OpenWriter(ChannelSpec spec);

    public IChannelReader OpenReader(ChannelSpec spec, PortEnd reader)
    {
        var core = Core(spec);
        if (!core.Readers.TryGetValue(reader, out var queue))
            throw new InvalidOperationException($"{reader} is not a reader of the Channel {spec.Id}.");
        return new ReaderEnd(spec, reader, queue);
    }

    // The Channels of one Module instance are closed when it stops: a reader
    // waiting returns, a writer waiting gives up.
    public void Close(string module)
    {
        foreach (var (id, core) in channels)
            if (core.Spec.Module == module && channels.TryRemove(id, out _))
                core.Close();
    }
}

// INPROCESS: writer and readers in one process. The writer puts each Message into
// every reader's bounded queue itself; nothing relays it, and it is shared, not
// copied (M3205 3.6.2).
public sealed class InProcessTransport : ChannelTransport
{
    public InProcessTransport(IClock? clock = null) : base(clock) { }

    public override string Name => "InProcess";

    public override IChannelWriter OpenWriter(ChannelSpec spec) => new Writer(Core(spec));

    private sealed class Writer(ChannelCore core) : IChannelWriter
    {
        public ChannelSpec Spec => core.Spec;
        public long Written => core.Written;

        public ValueTask<bool> WriteAsync(PortMessage message, int timeoutMs = -1, CancellationToken cancel = default)
        {
            core.Stamp(message);
            return core.DeliverAsync(message, timeoutMs, cancel);
        }
    }
}

// CONTROLLER: the Controller relays each Message from the writer to every reader,
// as the present implementation does, and can observe it on the way - the place
// for a recorder or an inspector. One relay per Channel keeps the order of its
// Messages.
public sealed class ControllerTransport : ChannelTransport
{
    public ControllerTransport(IClock? clock = null) : base(clock) { }

    public override string Name => "Controller";

    // Called with every Message the Controller relays.
    public Action<ChannelSpec, PortMessage>? Observer { get; set; }

    public long Relayed => Interlocked.Read(ref relayed);
    private long relayed;

    private readonly ConcurrentDictionary<string, Relay> relays = new();

    public override IChannelWriter OpenWriter(ChannelSpec spec)
    {
        var core = Core(spec);
        return new Writer(relays.GetOrAdd(spec.Id, _ => new Relay(this, core)), core);
    }

    private sealed class Writer(Relay relay, ChannelCore core) : IChannelWriter
    {
        public ChannelSpec Spec => core.Spec;
        public long Written => core.Written;

        public async ValueTask<bool> WriteAsync(PortMessage message, int timeoutMs = -1, CancellationToken cancel = default)
        {
            core.Stamp(message);
            return await relay.HandAsync(message, timeoutMs, cancel);
        }
    }

    // The Controller's hop: Messages handed over in order, observed, delivered.
    private sealed class Relay
    {
        private readonly ControllerTransport transport;
        private readonly ChannelCore core;
        private readonly SemaphoreSlim one = new(1, 1);

        public Relay(ControllerTransport transport, ChannelCore core)
        {
            this.transport = transport;
            this.core = core;
        }

        public async ValueTask<bool> HandAsync(PortMessage message, int timeoutMs, CancellationToken cancel)
        {
            await one.WaitAsync(cancel);
            try
            {
                transport.Observer?.Invoke(core.Spec, message);
                Interlocked.Increment(ref transport.relayed);
                return await core.DeliverAsync(message, timeoutMs, cancel);
            }
            finally { one.Release(); }
        }
    }
}

// ONE ORDER FOR STAMPS AND WHAT OBSERVES THEM: the lock under which a Message is
// stamped and observed, for every Channel of every transport of this process.
public static class StampOrder
{
    public static readonly object Gate = new();
}
