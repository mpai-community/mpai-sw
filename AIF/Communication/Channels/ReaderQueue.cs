using System.Diagnostics;

namespace AIF.Channels;

// ONE READER END: at most Depth Messages, a full end doing what its Overflow says,
// a Message older than MaxAge discarded rather than delivered; each counted
// (M3205 3.6.3, M3215 3.2). Both transports deliver into these; they differ in
// who puts a Message here - the writer itself, or the Controller relaying it.
internal sealed class ReaderQueue
{
    private readonly PortBehaviour behaviour;
    private readonly IClock clock;
    private readonly LinkedList<PortMessage> held = new();
    private readonly object gate = new();

    // Signalled when a Message arrives, and when one leaves (room for a Block-ed
    // writer). Replaced after each signal, so a waiter always waits on a fresh one.
    private TaskCompletionSource arrived = New();
    private TaskCompletionSource left = New();
    private bool closed;

    public long Dropped;
    public long Discarded;
    public long Taken;

    // Told of every Message this end loses - dropped by Overflow, discarded by
    // MaxAge - so that what it references can be released for this reader.
    private readonly Action<PortMessage>? lost;

    public ReaderQueue(PortBehaviour behaviour, IClock clock, Action<PortMessage>? lost = null)
    {
        this.behaviour = behaviour;
        this.clock = clock;
        this.lost = lost;
    }

    private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Pending { get { lock (gate) { Expire(); return held.Count; } } }

    public async ValueTask<bool> PutAsync(PortMessage message, int timeoutMs, CancellationToken cancel)
    {
        var until = timeoutMs < 0 ? long.MaxValue : Stopwatch.GetTimestamp() + timeoutMs * Stopwatch.Frequency / 1000;
        while (true)
        {
            Task room;
            lock (gate)
            {
                if (closed) return false;
                Expire();
                if (held.Count < Math.Max(1, behaviour.Depth))
                {
                    held.AddLast(message);
                    Signal(ref arrived);
                    return true;
                }
                switch (behaviour.Overflow)
                {
                    case Overflow.DropOldest:
                        lost?.Invoke(held.First!.Value);
                        held.RemoveFirst();
                        Dropped++;
                        held.AddLast(message);
                        Signal(ref arrived);
                        return true;
                    case Overflow.DropNewest:
                        lost?.Invoke(message);
                        Dropped++;
                        return true;
                }
                room = left.Task;
            }

            // Block: wait for room, within the writer's timeout.
            var remaining = until == long.MaxValue ? -1 : (int)Math.Max(0, (until - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);
            if (remaining == 0) return false;
            var waited = remaining < 0 ? room : await Task.WhenAny(room, Task.Delay(remaining, cancel));
            cancel.ThrowIfCancellationRequested();
            if (waited != room) return false;
        }
    }

    public bool TryTake(out PortMessage? message)
    {
        lock (gate)
        {
            Expire();
            if (held.First is { } first)
            {
                held.RemoveFirst();
                message = first.Value;
                Taken++;
                Signal(ref left);
                return true;
            }
            message = null;
            return false;
        }
    }

    public Task Arrival()
    {
        lock (gate)
        {
            Expire();
            return held.Count > 0 || closed ? Task.CompletedTask : arrived.Task;
        }
    }

    public async ValueTask<PortMessage?> TakeAsync(int timeoutMs, CancellationToken cancel)
    {
        var until = timeoutMs < 0 ? long.MaxValue : Stopwatch.GetTimestamp() + timeoutMs * Stopwatch.Frequency / 1000;
        while (true)
        {
            if (TryTake(out var message)) return message;
            Task wait;
            lock (gate) { if (closed) return null; wait = arrived.Task; }
            var remaining = until == long.MaxValue ? -1 : (int)Math.Max(0, (until - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);
            if (remaining == 0) return null;
            var done = remaining < 0 ? await Task.WhenAny(wait, Task.Delay(-1, cancel)) : await Task.WhenAny(wait, Task.Delay(remaining, cancel));
            cancel.ThrowIfCancellationRequested();
            if (done != wait) return TryTake(out message) ? message : null;
        }
    }

    public void Close()
    {
        lock (gate)
        {
            closed = true;
            arrived.TrySetResult();
            left.TrySetResult();
        }
    }

    // Called under the gate: Messages older than MaxAge are discarded.
    private void Expire()
    {
        if (behaviour.MaxAge is not { } maxAge) return;
        var oldest = clock.Monotonic - (long)(maxAge.TotalSeconds * Stopwatch.Frequency);
        var any = false;
        while (held.First is { } first && first.Value.Written < oldest)
        {
            lost?.Invoke(first.Value);
            held.RemoveFirst();
            Discarded++;
            any = true;
        }
        if (any) Signal(ref left);
    }

    private static void Signal(ref TaskCompletionSource source)
    {
        var done = source;
        source = New();
        done.TrySetResult();
    }
}
