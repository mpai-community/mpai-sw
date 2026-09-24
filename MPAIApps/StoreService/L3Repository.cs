using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mpai.StoreService;

// WHERE THE STORE KEEPS ITS L3s.
//
//   <root>/L3/<id>.json                     the latest version of each L3, one
//                                           folder a Controller can read as it is
//   <root>/versions/<id>/<n>.json           every version ever published
//   <root>/versions/<id>/<n>.findings.json  what the Store found in it, and when
//
// A published L3 is never overwritten: a replacement is version n+1. The index
// is rebuilt from the files at start, so the folders are the whole truth.
public sealed class L3Repository
{
    public sealed record Entry(string Id, int Version, DateTimeOffset Published, int Errors, int Warnings);
    private sealed record Record(DateTimeOffset Published, List<Finding> Findings);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object gate = new();
    private readonly Dictionary<string, List<Entry>> index = new(StringComparer.Ordinal);

    public string Root { get; }
    public string LatestFolder => Path.Combine(Root, "L3");
    private string VersionsFolder => Path.Combine(Root, "versions");

    public L3Repository(string root)
    {
        Root = root;
        Directory.CreateDirectory(LatestFolder);
        Directory.CreateDirectory(VersionsFolder);
        foreach (var dir in Directory.EnumerateDirectories(VersionsFolder))
        {
            var id = Path.GetFileName(dir);
            foreach (var file in Directory.EnumerateFiles(dir, "*.findings.json"))
            {
                if (!int.TryParse(Path.GetFileName(file).Split('.')[0], out var n)) continue;
                var r = JsonSerializer.Deserialize<Record>(File.ReadAllText(file));
                if (r is null) continue;
                Add(new Entry(id, n, r.Published, r.Findings.Count(f => f.Severity == "error"), r.Findings.Count(f => f.Severity != "error")));
            }
        }
    }

    public int Count { get { lock (gate) return index.Count; } }

    public Entry Publish(string id, string l3, List<Finding> findings)
    {
        lock (gate)
        {
            var n = index.TryGetValue(id, out var list) ? list.Max(e => e.Version) + 1 : 1;
            var dir = Path.Combine(VersionsFolder, id);
            Directory.CreateDirectory(dir);
            var now = DateTimeOffset.UtcNow;
            File.WriteAllText(Path.Combine(dir, $"{n}.json"), l3);
            File.WriteAllText(Path.Combine(dir, $"{n}.findings.json"), JsonSerializer.Serialize(new Record(now, findings), Json));
            File.WriteAllText(Path.Combine(LatestFolder, id + ".json"), l3);
            var entry = new Entry(id, n, now, findings.Count(f => f.Severity == "error"), findings.Count(f => f.Severity != "error"));
            Add(entry);
            return entry;
        }
    }

    public IReadOnlyList<Entry> Latest(string? name)
    {
        lock (gate)
            return index.Values.Select(v => v.MaxBy(e => e.Version)!)
                        .Where(e => name is null || Matches(e.Id, name))
                        .OrderBy(e => e.Id).ToList();
    }

    public IReadOnlyList<Entry> Versions(string id)
    {
        lock (gate) return index.TryGetValue(id, out var list) ? list.OrderBy(e => e.Version).ToList() : new List<Entry>();
    }

    public string? Text(string id, int? version)
    {
        var n = version ?? Versions(id).LastOrDefault()?.Version;
        if (n is null) return null;
        var path = Path.Combine(VersionsFolder, id, $"{n}.json");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public List<Finding>? Findings(string id, int? version)
    {
        var n = version ?? Versions(id).LastOrDefault()?.Version;
        if (n is null) return null;
        var path = Path.Combine(VersionsFolder, id, $"{n}.findings.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<Record>(File.ReadAllText(path))?.Findings : null;
    }

    // An AIM name asked for matches an Instance by its identifier, or by the
    // standard name the identifier implements: MMC-TIQ-V2.5 finds 1MMC-TIQ-V2.5-I01.
    public static string StandardName(string id) =>
        Regex.Replace(Regex.Replace(id, @"^[0-9]+", ""), @"-I[0-9]+$", "");

    private static bool Matches(string id, string name) =>
        id.Equals(name, StringComparison.OrdinalIgnoreCase) ||
        StandardName(id).Equals(name, StringComparison.OrdinalIgnoreCase);

    private void Add(Entry e)
    {
        if (!index.TryGetValue(e.Id, out var list)) index[e.Id] = list = new List<Entry>();
        list.Add(e);
    }
}
