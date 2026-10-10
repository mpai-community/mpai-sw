using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using AIF.Channels;
using AIF.SharedStorage;

namespace AIF.Controller;

// THE RECORD OF A MODULE'S BOUNDARY (M3219 3.4). What the Controller saw cross
// the boundary - each Message written to a boundary Input Port, each Message a
// boundary Output Port received - with its stamp, written into the Private
// Storage of the Module, in the category Boundary, for the User Agent to read.
// The Controller is its writer: the User Agent cannot write into it.
//
// Recording never holds the Module. A Message is taken as it crosses - its
// payloads read before they may be released - and handed to a writer of its own
// through a bounded queue; one the queue cannot take is not recorded, and
// counted. The form of the record is this Implementation's.
public sealed class BoundaryRecord
{
    public const string Category = "Boundary";
    private const int InlineAbove = 64 * 1024;   // an inline datum above this becomes a payload of its own

    private readonly IRuledStorage storage;
    private readonly PayloadStore payloads;
    private readonly IClock clock;
    private readonly JsonObject header;
    private readonly Channel<(string Key, byte[] Data, PortKey Port)> queue;
    private readonly Task writing;
    private readonly TimeSpan writeDelay;
    private readonly ConcurrentDictionary<PortKey, Counts> counts = new();
    private readonly DateTimeOffset started;
    private readonly long startedTicks;
    private long sequence;
    private long payloadNumber;

    public string Id { get; }
    public bool Always { get; }

    public readonly record struct PortKey(string Direction, string DataType, int PortNumber);

    public sealed class Counts
    {
        public long Recorded;
        public long NotRecorded;
    }

    // The Messages recorded and those not recorded, per Port.
    public IReadOnlyDictionary<PortKey, (long Recorded, long NotRecorded)> Totals =>
        counts.ToDictionary(c => c.Key, c => (Interlocked.Read(ref c.Value.Recorded), Interlocked.Read(ref c.Value.NotRecorded)));

    public BoundaryRecord(IRuledStorage storage, PayloadStore payloads, IClock clock, JsonObject header,
                          bool always, int depth, TimeSpan writeDelay)
    {
        this.storage = storage;
        this.payloads = payloads;
        this.clock = clock;
        this.header = header;
        this.writeDelay = writeDelay;
        Always = always;
        started = clock.Now;
        startedTicks = clock.Monotonic;
        Id = $"R{started:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..4]}";
        queue = Channel.CreateBounded<(string, byte[], PortKey)>(new BoundedChannelOptions(Math.Max(1, depth))
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.Wait
        });
        writing = Task.Run(WriteAsync);
    }

    // A Message as it crosses: In, written to a boundary Input Port; Out, received
    // by a boundary Output Port - the boundary's Port, not the Port of the AIM that
    // wrote it.
    public void Take(string direction, PortMessage message, PortEnd? boundary = null)
    {
        var port = new PortKey(direction, boundary?.DataType ?? message.DataType, boundary?.PortNumber ?? message.PortNumber);
        var mine = counts.GetOrAdd(port, _ => new Counts());
        var seq = Interlocked.Increment(ref sequence);

        // The payloads, now: a reference may be released as soon as it is read.
        var extracted = new List<(string Key, byte[] Data)>();
        var json = Extract(message.Json, extracted);
        var record = new JsonObject
        {
            ["Sequence"]   = seq,
            ["Stamp"]      = message.Stamp.ToString("O"),
            ["SinceStart"] = System.Diagnostics.Stopwatch.GetElapsedTime(startedTicks, clock.Monotonic).TotalMilliseconds,
            ["Direction"]  = direction,
            ["DataType"]   = port.DataType,
            ["PortNumber"] = port.PortNumber,
            ["Json"]       = json
        };

        // All or nothing: a record the queue cannot take now, with its payloads,
        // is not recorded.
        var items = extracted.Select(p => (p.Key, p.Data)).Append(($"{Id}/{seq:D9}", Encoding.UTF8.GetBytes(record.ToJsonString()))).ToList();
        foreach (var (key, data) in items)
            if (!queue.Writer.TryWrite((key, data, port)))
            {
                Interlocked.Increment(ref mine.NotRecorded);
                return;
            }
        Interlocked.Increment(ref mine.Recorded);
    }

    // The payloads of an Object - by reference, or inline above a size - each a
    // datum of its own, referenced from the Object as record:payload/<n>.
    private string Extract(string json, List<(string Key, byte[] Data)> extracted)
    {
        var references = PayloadStore.ReferencesIn(json);
        if (references.Count == 0 && json.Length <= InlineAbove) return json;
        JsonNode? root;
        try { root = JsonNode.Parse(json); } catch { return json; }
        if (root is null) return json;
        Walk(root);
        return root.ToJsonString();

        void Walk(JsonNode node)
        {
            switch (node)
            {
                case JsonObject o:
                    byte[]? bytes = null;
                    if (o["DataURI"] is JsonValue uri && uri.TryGetValue<string>(out var u) && PayloadStore.IsReference(u) && payloads.TryGet(u, out var held))
                        bytes = held.ToArray();
                    else if (o["Data"] is JsonValue data && data.TryGetValue<string>(out var d) && d.Length > InlineAbove)
                        try { bytes = Convert.FromBase64String(d); } catch { }
                    if (bytes is not null)
                    {
                        var key = $"{Id}/payload/{Interlocked.Increment(ref payloadNumber)}";
                        extracted.Add((key, bytes));
                        o.Remove("Data");
                        o["DataLength"] = bytes.Length;
                        o["DataURI"] = "record:payload/" + key[(Id.Length + 9)..];
                    }
                    foreach (var (_, child) in o.ToList()) if (child is not null) Walk(child);
                    break;
                case JsonArray a:
                    foreach (var child in a) if (child is not null) Walk(child);
                    break;
            }
        }
    }

    private async Task WriteAsync()
    {
        await foreach (var (key, data, port) in queue.Reader.ReadAllAsync())
        {
            if (writeDelay > TimeSpan.Zero) await Task.Delay(writeDelay);
            try
            {
                storage.MPAI_AIFM_RuledStorage_Put(key, data, Category, [RuledStore.UserAgent]);
            }
            catch
            {
                // Where the storage cannot take it, the record is not written.
                var mine = counts.GetOrAdd(port, _ => new Counts());
                Interlocked.Increment(ref mine.NotRecorded);
                Interlocked.Decrement(ref mine.Recorded);
            }
        }
    }

    // The record ends: what is queued is written, then the header, with the
    // counts.
    public async Task<IReadOnlyDictionary<PortKey, (long Recorded, long NotRecorded)>> StopAsync()
    {
        queue.Writer.TryComplete();
        await writing;
        var totals = Totals;
        header["Record"]  = Id;
        header["Started"] = started.ToString("O");
        header["Stopped"] = clock.Now.ToString("O");
        header["Ports"]   = new JsonArray(totals.OrderBy(t => t.Key.Direction).ThenBy(t => t.Key.DataType).ThenBy(t => t.Key.PortNumber)
            .Select(t => (JsonNode)new JsonObject
            {
                ["Direction"] = t.Key.Direction, ["DataType"] = t.Key.DataType, ["PortNumber"] = t.Key.PortNumber,
                ["Recorded"] = t.Value.Recorded, ["NotRecorded"] = t.Value.NotRecorded
            }).ToArray());
        try { storage.MPAI_AIFM_RuledStorage_Put($"{Id}/header", Encoding.UTF8.GetBytes(header.ToJsonString()), Category, [RuledStore.UserAgent]); }
        catch { /* no storage: the counts are returned all the same */ }
        return totals;
    }
}
