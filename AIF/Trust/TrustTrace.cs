using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AIF.Trust;

// THE TRACE OF A CONTROLLER'S TRUST DECISIONS (M3223 3.6; M3205 7.3, Auditability).
// Every Trust Operation (PTF-TOP) the Controller makes - an identity issued, a
// lifecycle changed, a policy bound, each check of the Verification Pipeline, each
// check of a party on a link - is a record of its Trace: an AIF Trace (AIF-TRC-V3.0)
// that carries the operation, its place in the sequence, and the hash of the record
// before it; the first carries the hash of the Controller's Trust Anchor object,
// so the chain begins at the anchor. Each record is signed by the Controller. A
// record removed, reordered, inserted or changed breaks the chain where it is; a
// record cut from the end is evident against a head - the sequence and hash of the
// last record - kept or published elsewhere.
//
// Kept in memory, and - where a file is given - one record a line, appended as it
// is made. A Controller that starts again on its file continues its chain, if the
// chain is intact; if it is not, it does not start.
public sealed class TrustTrace
{
    public const string Header = "AIF-TRC-V3.0";

    private readonly TrustAnchorKey anchor;
    private readonly ECDsa key;
    private readonly string? file;
    private readonly Func<DateTimeOffset> now;
    private readonly object gate = new();
    private readonly List<JsonObject> records = new();
    private string previous;

    public TrustTrace(TrustAnchorKey anchor, ECDsa key, Func<DateTimeOffset> now, string? file = null)
    {
        this.anchor = anchor;
        this.key = key;
        this.now = now;
        this.file = file;
        previous = Genesis(anchor.Object());
        if (file is not null && File.Exists(file))
        {
            var kept = Load(file);
            if (Verify(kept, anchor.Object()) is { } broken)
                throw new InvalidDataException($"The Trace at {file} is not intact: {broken}. The Controller does not continue it.");
            records.AddRange(kept);
            if (kept.Count > 0) previous = HashOf(kept[^1]);
        }
    }

    public IReadOnlyList<JsonObject> Records { get { lock (gate) return records.ToList(); } }

    // The last record's sequence and hash: what a verifier compares a Trace with to
    // see that nothing was cut from its end.
    public (long Sequence, string Hash) Head { get { lock (gate) return (records.Count, previous); } }

    public JsonObject Append(JsonObject operation)
    {
        lock (gate)
        {
            var sequence = records.Count + 1;
            var t = now().ToUnixTimeMilliseconds();
            var record = new JsonObject
            {
                ["Header"] = Header,
                ["TraceID"] = $"{anchor.AnchorId}#TRC-{sequence}",
                ["TraceTime"] = new JsonObject
                {
                    ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = $"{anchor.AnchorId}#TRC-{sequence}#Time",
                    ["SimpleTimeData"] = new JsonArray(new JsonObject
                    {
                        ["FlagsByte"] = 3, ["StartTime"] = t, ["EndTime"] = t, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 0
                    })
                },
                ["Source"] = new JsonArray(new JsonObject { ["ProcessID"] = anchor.AnchorId }),
                ["Sequence"] = sequence,
                ["Previous"] = new JsonObject { ["HashAlgorithm"] = PtfIdentity.HashAlgorithm, ["Hash"] = previous },
                ["TrustOperation"] = operation.DeepClone()
            };
            PtfSignature.Sign(record, key, anchor.AnchorId);
            records.Add(record);
            previous = HashOf(record);
            if (file is not null) File.AppendAllText(file, record.ToJsonString() + "\n");
            return record;
        }
    }

    // The hash that begins a chain: of the Controller's Trust Anchor object.
    public static string Genesis(JsonObject anchorObject) => Convert.ToHexString(SHA256.HashData(PtfCanonical.Bytes(anchorObject)));

    public static string HashOf(JsonObject record) => Convert.ToHexString(SHA256.HashData(PtfCanonical.Bytes(record)));

    public static List<JsonObject> Load(string file) =>
        File.ReadAllLines(file).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();

    // A TRACE, VERIFIED: null where it is intact - and, where a head is given, ends
    // there; otherwise where and how it is broken.
    public static string? Verify(IReadOnlyList<JsonObject> records, JsonObject anchorObject, (long Sequence, string Hash)? head = null)
    {
        var anchorId = (string?)anchorObject["AnchorID"] ?? "";
        using var anchorKey = TrustAnchorKey.KeyOf(anchorObject);
        if (anchorKey is null) return "its anchor has no key";
        var expected = Genesis(anchorObject);
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            var n = i + 1;
            if ((string?)r["Header"] != Header) return $"record {n}: not a Trace";
            if (PtfSignature.Verify(r, id => id == anchorId ? anchorKey : null) != PtfSignature.Outcome.Valid)
                return $"record {n}: altered, or not the Controller's (its signature does not verify)";
            if ((long?)r["Sequence"] != n)
                return $"record {n}: its sequence is {r["Sequence"]} - records are missing, or out of their order";
            if (!string.Equals((string?)r["Previous"]?["Hash"], expected, StringComparison.OrdinalIgnoreCase))
                return $"record {n}: it does not follow record {n - 1} - a record was removed, reordered or changed";
            if (r["TrustOperation"] is not JsonObject operation || PtfSignature.Verify(operation, id => id == anchorId ? anchorKey : null) != PtfSignature.Outcome.Valid)
                return $"record {n}: its Trust Operation is not the Controller's";
            expected = HashOf(r);
        }
        if (head is { } h && (h.Sequence != records.Count || !string.Equals(h.Hash, expected, StringComparison.OrdinalIgnoreCase)))
            return h.Sequence > records.Count
                ? $"it ends at record {records.Count}, its head is record {h.Sequence}: records were cut from its end"
                : "it does not end at its head";
        return null;
    }
}
