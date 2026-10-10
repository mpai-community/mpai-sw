namespace AIF.Channels;

// CHANNELS (M3205 3.6.1, M3215 3.1). A Channel joins one Output Port to the Input
// Ports the Topology connects to it. It has an identity - the Module instance,
// and the AIM Instance, Data Type and Port Number of its writer - a behaviour at
// each reader, and a transport. Every Port is addressed by AIM Instance, Data
// Type and Port Number; a Port name never reaches a Channel.

// One end of a Channel: an AIM Instance's Port, or a boundary Port of the Module
// (Aim empty).
public readonly record struct PortEnd(string Aim, string DataType, int PortNumber = 1)
{
    public bool IsBoundary => string.IsNullOrEmpty(Aim);
    public override string ToString() => $"{(IsBoundary ? "(boundary)" : Aim)}.{DataType}#{PortNumber}";
}

// What a write to a full reader end does (M3205 3.6.3).
public enum Overflow
{
    Block,
    DropOldest,
    DropNewest
}

// The behaviour of one reader end: Depth, Overflow, MaxAge. A Port that declares
// none is Block, Depth 16, no MaxAge (M3215 3.2): a writer waits rather than lose
// a Message, as an exchange needs; 16 bounds memory in continuous execution; in
// an exchange a Message does not age.
public sealed record PortBehaviour(int Depth, Overflow Overflow, TimeSpan? MaxAge)
{
    public const int DefaultDepth = 16;

    public static PortBehaviour Default { get; } = new(DefaultDepth, Overflow.Block, null);
}

// A Message on a Channel (M3215 3.1). Immutable once written: shared, not copied,
// among the readers of one process. The stamp is the Controller's, given by the
// writer's end; the writer supplies none (M3205 3.6.4). Payloads names the
// references its Object carries (3.6).
public sealed class PortMessage
{
    public required string DataType { get; init; }
    public int PortNumber { get; init; } = 1;
    public required string Json { get; init; }
    public IReadOnlyList<string> Payloads { get; init; } = Array.Empty<string>();

    // The one Message of a boundary write that the record of the boundary takes, where
    // the write goes to several Channels.
    public bool RecordAsInput { get; init; }

    // FROM ANOTHER CONTROLLER (M3205 3.3; M3241 3.2): the Controller that wrote it -
    // its controllerID - and its stamp there. Null for a Message written here.
    public string? From { get; init; }
    public DateTimeOffset? FromStamp { get; init; }

    // Set by the writer's end.
    public DateTimeOffset Stamp { get; internal set; }
    public long Written { get; internal set; }          // monotonic, Stopwatch ticks
    public long Sequence { get; internal set; }
}

// A Channel's identity, its writer and readers, each reader's behaviour, and its
// transport.
public sealed record ChannelSpec(
    string Module,
    PortEnd Writer,
    IReadOnlyList<ChannelReaderSpec> Readers,
    string Transport)
{
    public string Id => $"{Module}/{Writer}";
}

public sealed record ChannelReaderSpec(PortEnd Reader, PortBehaviour Behaviour);

// The only way to a Channel end. The Controller opens every end through a
// transport and gives each AIM, and the User Agent at the boundary, only its own.
public interface IChannelTransport
{
    string Name { get; }
    IChannelWriter OpenWriter(ChannelSpec spec);
    IChannelReader OpenReader(ChannelSpec spec, PortEnd reader);
}

public interface IChannelWriter
{
    ChannelSpec Spec { get; }

    // False when the write could not complete within timeoutMs (0: do not wait;
    // negative: wait without limit) - a reader Block-ing and full.
    ValueTask<bool> WriteAsync(PortMessage message, int timeoutMs = -1, CancellationToken cancel = default);

    // How many Messages were written on the Channel.
    long Written { get; }
}

public interface IChannelReader
{
    ChannelSpec Spec { get; }
    PortEnd Reader { get; }

    // The oldest Message pending, within timeoutMs; null when none came.
    ValueTask<PortMessage?> ReadAsync(int timeoutMs = -1, CancellationToken cancel = default);

    // The oldest Message pending, if one is, without waiting.
    bool TryRead(out PortMessage? message);

    // Completes when a Message is pending (or the Channel is closed).
    Task WaitAsync(CancellationToken cancel = default);

    int  Pending   { get; }
    long Taken     { get; }   // read
    long Dropped   { get; }   // lost by Overflow
    long Discarded { get; }   // older than MaxAge
}

// The time a Channel stamps with. The Controller reads time through this one
// interface; the time base is whatever clock is plugged in (M3215 3.5).
public interface IClock
{
    DateTimeOffset Now { get; }
    long Monotonic { get; }                       // in Stopwatch ticks (Stopwatch.Frequency a second)
}

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
    public long Monotonic => System.Diagnostics.Stopwatch.GetTimestamp();
}

// ANOTHER MACHINE'S TIME BASE (M3217 3.2): this machine's clock moved by the
// offset measured to the Controller's, so that what is stamped here is stamped
// on the Controller's time base. Monotonic time stays this machine's own.
public sealed class OffsetClock(IClock local) : IClock
{
    public IClock Local { get; } = local;
    public TimeSpan Offset { get; set; }
    public DateTimeOffset Now => Local.Now + Offset;
    public long Monotonic => Local.Monotonic;
}
