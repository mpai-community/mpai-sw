using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mpai.StoreService;

// VALIDATION OF AN L3 AGAINST ITS L2. An L2 is the standard-level instance of an
// AIM (schemas/<standard>/V<n>/AIMs/*.json, e.g. MMC-AMQ-V2.5); an L3 describes
// one implementation of it (1MMC-AMQ-V2.5-I01). What differs is SIGNALLED, never
// refused: an implementation may legitimately go beyond its L2, and where it
// does, the findings say so.
public sealed class L2Check
{
    private readonly Dictionary<string, (string File, JsonElement L2)> l2s = new(StringComparer.OrdinalIgnoreCase);
    public int Count => l2s.Count;

    public L2Check(string schemasRoot)
    {
        if (!Directory.Exists(schemasRoot)) return;
        foreach (var dir in Directory.EnumerateDirectories(schemasRoot, "AIMs", SearchOption.AllDirectories))
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var l2 = JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone();
                    if (l2.TryGetProperty("Identifier", out var id) && id.TryGetProperty("AIMName", out var n) && n.GetString() is { Length: > 0 } name)
                        l2s[name] = (Path.GetRelativePath(schemasRoot, file), l2);
                }
                catch { /* not an L2 */ }
            }
    }

    // 1MMC-AMQ-V2.5-I01 implements MMC-AMQ-V2.5.
    public static string StandardName(string id) =>
        Regex.Replace(Regex.Replace(id, @"^[0-9]+", ""), @"-I[0-9]+$", "");

    public IEnumerable<Finding> Check(string id, JsonElement l3)
    {
        var standard = StandardName(id);
        if (!l2s.TryGetValue(standard, out var found))
        {
            yield return new Finding("L2", "warning", $"No L2 for {standard} in the published schemas: not validated.");
            yield break;
        }
        var (file, l2) = found;

        if (l3.TryGetProperty("Identifier", out var ident))
        {
            string T(string k) => ident.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
            if (T("ImplementerID") + T("ImplementationID") != id)
                yield return new Finding("L2", "warning", $"Identifier: ImplementerID + ImplementationID ({T("ImplementerID")}{T("ImplementationID")}) is not the AIMName ({id}).");
        }

        var p2 = Ports(l2); var p3 = Ports(l3);
        foreach (var p in p3.Except(p2)) yield return new Finding("L2", "warning", $"Port {p} is not in the L2 ({file}).");
        foreach (var p in p2.Except(p3)) yield return new Finding("L2", "warning", $"Port {p} of the L2 ({file}) is not in the L3.");

        var s2 = SubAims(l2, standardise: false); var s3 = SubAims(l3, standardise: true);
        foreach (var s in s3.Except(s2)) yield return new Finding("L2", "warning", $"Sub-AIM {s} is not in the L2 ({file}).");
        foreach (var s in s2.Except(s3)) yield return new Finding("L2", "warning", $"Sub-AIM {s} of the L2 ({file}) is not in the L3.");
    }

    // A Port, as the L2 and L3 must agree on it: direction, Data Type(s), number.
    private static HashSet<string> Ports(JsonElement aim)
    {
        var set = new HashSet<string>();
        if (!aim.TryGetProperty("ExternalPorts", out var ports) || ports.ValueKind != JsonValueKind.Array) return set;
        foreach (var p in ports.EnumerateArray())
        {
            var dir = p.TryGetProperty("Direction", out var d) ? d.GetString() : "?";
            var type = !p.TryGetProperty("DataType", out var t) ? "?"
                     : t.ValueKind == JsonValueKind.Array ? string.Join("|", t.EnumerateArray().Select(x => x.GetString()))
                     : t.GetString();
            var n = p.TryGetProperty("PortNumber", out var pn) && pn.ValueKind == JsonValueKind.Number ? pn.GetInt32() : 1;
            set.Add($"{dir} {type}{(n == 1 ? "" : ":" + n)}");
        }
        return set;
    }

    private static HashSet<string> SubAims(JsonElement aim, bool standardise)
    {
        var set = new HashSet<string>();
        if (!aim.TryGetProperty("SubAIMs", out var subs) || subs.ValueKind != JsonValueKind.Array) return set;
        foreach (var s in subs.EnumerateArray())
            if (s.TryGetProperty("Identifier", out var i) && i.TryGetProperty("AIMName", out var n) && n.GetString() is { } name)
                set.Add(standardise ? StandardName(name) : name);
        return set;
    }
}
