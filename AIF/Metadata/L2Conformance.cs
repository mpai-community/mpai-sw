using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIF.Metadata;

// AN L3 IS AN INSTANCE OF ITS L2. The schema checks the form of one file; this
// checks that an Implementation's L3 is what the Standard's L2 allows. The L2 is
// the one the L3's Header names.
//
// An L3 declares only what its Implementation supports. So:
//   1. every Port of the L3 is a Port of the L2: the same Direction, and the L2's
//      Data Type(s) include the L3's (an L2 Port may accept a Basic and a Full
//      Object, an L3 Port one of them);
//   2. an Input both declare is optional in both, or in neither; an Output the L2
//      requires is not optional in the L3 (an L3 may always produce an Output the
//      L2 makes optional: it gives more, not less);
//   3. a Port of the L2 that the L3 does not declare is optional in the L2;
//   4. every Sub-AIM of a composite L3 is a Sub-AIM of its L2, or a composite whose
//      Sub-AIMs include one, at any depth - a Module may use a composite AIM where
//      its Standard names one of the AIMs that composite contains -
//   5. or a combination of Sub-AIMs of the L2 that exposes their interface (below).
// Ports are matched by Direction and Data Type; names are labels, but where two
// Ports could match, the one of the same name is preferred.
public sealed class L2Conformance
{
    private readonly Dictionary<string, JsonElement> l2s = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, JsonElement?> findL3;

    // schemasRoot: the folder holding */V*/AIMs/*.json (the L2s).
    // findL3: an L3 by its AIM Instance identifier, to look inside a composite Sub-AIM.
    public L2Conformance(string schemasRoot, Func<string, JsonElement?> findL3)
    {
        this.findL3 = findL3;
        foreach (var file in Directory.EnumerateFiles(schemasRoot, "*.json", SearchOption.AllDirectories)
                                      .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "AIMs"))
        {
            try
            {
                var l2 = JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone();
                if (l2.TryGetProperty("Identifier", out var id) && id.TryGetProperty("AIMName", out var n) &&
                    n.GetString() is { Length: > 0 } name)
                    l2s[name] = l2;
            }
            catch { /* not an L2 */ }
        }
    }

    public int Count => l2s.Count;

    // The AIM Type an L3 implements: its Header, else its AIMName without the
    // ImplementerID and the Instance number.
    public static string TypeOf(JsonElement l3) =>
        l3.TryGetProperty("Header", out var h) && h.GetString() is { Length: > 0 } header
            ? header
            : TypeOf(l3.GetProperty("Identifier").GetProperty("AIMName").GetString() ?? "");

    public static string TypeOf(string instance) =>
        Regex.Replace(Regex.Replace(instance, @"^[0-9A-Za-z]*?(?=[A-Z]{3}-[A-Z]{3}-V)", ""), @"-I[0-9]+$", "");

    // What in the L3 its L2 does not allow; none when it conforms.
    public IReadOnlyList<string> Check(JsonElement l3)
    {
        var type = TypeOf(l3);
        if (!l2s.TryGetValue(type, out var l2))
            return [$"No L2 of {type}: an L3 is validated against its L2, and there is none."];

        var found = new List<string>();
        // The Header names the type: the L3's is its L2's.
        var header2 = l2.TryGetProperty("Header", out var h2) && h2.ValueKind == JsonValueKind.String ? h2.GetString() : null;
        var header3 = l3.TryGetProperty("Header", out var h3) && h3.ValueKind == JsonValueKind.String ? h3.GetString() : null;
        if (header2 is not null && header3 != header2)
            found.Add(header3 is null ? $"The L3 has no Header; its L2's is {header2}." : $"Header {header3}: its L2's is {header2}.");
        var p2 = Ports(l2); var p3 = Ports(l3);
        var used = new HashSet<int>();
        foreach (var x in p3)
        {
            var candidates = p2.Select((y, i) => (y, i))
                .Where(c => !used.Contains(c.i) && c.y.Direction == x.Direction && x.Types.IsSubsetOf(c.y.Types)).ToList();
            // Paired by Port Number, never by name: the L2's Port n of that type is the L3's.
            var pick = candidates.FirstOrDefault(c => c.y.Number == x.Number);
            if (pick.y is null) pick = candidates.FirstOrDefault();
            if (pick.y is null) { found.Add($"{x}: the L2 has no such Port."); continue; }
            used.Add(pick.i);
            var disagrees = x.Direction == "Output" ? x.Optional && !pick.y.Optional : pick.y.Optional != x.Optional;
            if (disagrees)
                found.Add($"{x}: {(x.Optional ? "optional" : "required")} here, {(pick.y.Optional ? "optional" : "required")} in the L2.");
            // Technology is the L2's: from it one knows an AIM is Hardware ("" is Software).
            if (x.Technology != pick.y.Technology)
                found.Add($"{x}: Technology {x.Technology} here, {pick.y.Technology} in the L2.");
            // IsRemote in the L2 is a permission: a Port the L2 does not let be remote is not.
            if (x.IsRemote && !pick.y.IsRemote)
                found.Add($"{x}: remote here, and the L2 does not let it be remote.");
        }
        for (var i = 0; i < p2.Count; i++)
            if (!used.Contains(i) && !p2[i].Optional)
                found.Add($"{p2[i]} of the L2 is not declared, and the L2 does not make it optional.");

        found.AddRange(ResourcePolicies(l3, l2));
        found.AddRange(Implementations(l3, l2));
        found.AddRange(Filled(l3, "ResourcePolicies"));
        found.AddRange(Filled(l3, "Implementations"));

        var subs2 = SubAims(l2).Select(TypeOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in SubAims(l3))
            if (!subs2.Contains(TypeOf(sub)) && !Contains(sub, subs2, 0) && !Combines(TypeOf(sub), l2, subs2))
                found.Add($"Sub-AIM {sub}: not a Sub-AIM of the L2, nor a composite containing one, nor a combination of Sub-AIMs of the L2 exposing their interface.");
        return found;
    }

    // WHAT THE L2 LISTS, THE L3 CHOOSES FROM - as the L2s write it: the options
    // inside the placeholder, "/* x86-64 | ARM64 */", "/* Low | Medium | High */"; a
    // placeholder without options ("/* GB */", "/* string */") gives only the form
    // of what the implementer writes. The Resource Policies an L3 states are among
    // those its L2 lists, and each value among the L2's options where it lists any.
    private static IEnumerable<string> ResourcePolicies(JsonElement l3, JsonElement l2)
    {
        var listed = Items(l2, "ResourcePolicies").Where(r => Text(r, "Name") is { Length: > 0 } n && !IsPlaceholder(n))
                                                  .GroupBy(r => Text(r, "Name")).ToDictionary(g => g.Key, g => g.First());
        if (listed.Count == 0) yield break;
        foreach (var policy in Items(l3, "ResourcePolicies"))
        {
            var name = Text(policy, "Name");
            if (!listed.TryGetValue(name, out var allowed))
            {
                yield return $"Resource Policy {name}: not one the L2 lists ({string.Join(", ", listed.Keys)}).";
                continue;
            }
            foreach (var field in new[] { "Minimum", "Maximum", "Request" })
                if (Options(Text(allowed, field)) is { } options && policy.TryGetProperty(field, out var v) &&
                    v.ValueKind == JsonValueKind.String && !options.Contains(v.GetString() ?? ""))
                    yield return $"Resource Policy {name} {field} {v.GetString()}: not one the L2 lists ({string.Join(" | ", options)}).";
        }
    }

    // The Implementations: each Architecture, Operating System and Source among the
    // options of the L2's Implementations.
    private static IEnumerable<string> Implementations(JsonElement l3, JsonElement l2)
    {
        foreach (var field in new[] { "Architecture", "OperatingSystem", "Source" })
        {
            var templates = Items(l2, "Implementations").Select(i => Text(i, field)).ToList();
            if (templates.Count == 0) continue;
            var options = new HashSet<string>(StringComparer.Ordinal);
            var free = false;
            foreach (var template in templates)
            {
                if (!IsPlaceholder(template)) { if (template.Length > 0) options.Add(template); else free = true; }
                else if (Options(template) is { } listed) options.UnionWith(listed);
                else free = true;
            }
            if (free || options.Count == 0) continue;
            foreach (var chosen in Items(l3, "Implementations").Select(i => Text(i, field)).Where(v => v.Length > 0).Distinct())
                if (!options.Contains(chosen))
                    yield return $"Implementation {field} {chosen}: not one the L2 lists ({string.Join(" | ", options)}).";
        }
    }

    // The options a placeholder lists ("/* A | B */" gives A, B); null when it lists none.
    private static IReadOnlyList<string>? Options(string placeholder)
    {
        if (!IsPlaceholder(placeholder)) return null;
        var inner = placeholder.Trim().TrimStart('/').TrimStart('*').TrimEnd('/').TrimEnd('*').Trim();
        if (!inner.Contains('|')) return null;
        var options = inner.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return options.All(o => System.Text.RegularExpressions.Regex.IsMatch(o, "^[A-Za-z0-9-]+$")) ? options : null;
    }

    // What the L2 leaves to the implementer, the L3 states: no placeholder is left in
    // its Resource Policies and Implementations.
    private static IEnumerable<string> Filled(JsonElement l3, string list)
    {
        foreach (var item in Items(l3, list))
            foreach (var p in item.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && IsPlaceholder(p.Value.GetString() ?? ""))
                    yield return $"{list}: {p.Name} is left as the placeholder {p.Value.GetString()}.";
    }

    private static IEnumerable<JsonElement> Items(JsonElement aim, string list) =>
        aim.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object)
            : [];

    private static string Text(JsonElement item, string field) =>
        item.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool IsPlaceholder(string value) => value.TrimStart().StartsWith("/*", StringComparison.Ordinal);

    // 5. A COMBINATION. Two or more AIMs may be combined into one, provided that the
    //    result exposes the same interface. A Sub-AIM whose own L2 is a composite of
    //    Sub-AIMs of the parent's L2 conforms if its Ports are the interface that
    //    group exposes in the parent: the Topology lines of the parent that cross the
    //    group's border - in, its inputs; out, its outputs - each typed by the
    //    parent's own labels (its ExternalPorts and InternalTypes).
    private bool Combines(string type, JsonElement parent, HashSet<string> parentSubs)
    {
        if (!l2s.TryGetValue(type, out var combined)) return false;
        var group = SubAims(combined).Select(TypeOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (group.Count < 2 || !group.IsSubsetOf(parentSubs)) return false;

        var labels = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var p in Ports(parent)) labels.TryAdd(p.Name, p.Types);
        if (parent.TryGetProperty("InternalTypes", out var its) && its.ValueKind == JsonValueKind.Array)
            foreach (var it in its.EnumerateArray())
                if (it.TryGetProperty("Name", out var n) && n.GetString() is { Length: > 0 } name)
                    labels[name] = TypesOf(it);

        var exposed = new List<(string Direction, HashSet<string> Types)>();
        if (!parent.TryGetProperty("Topology", out var topology) || topology.ValueKind != JsonValueKind.Array) return false;
        foreach (var line in topology.EnumerateArray())
        {
            var (fromAim, fromLabel) = End(line, "Output");
            var (toAim, toLabel) = End(line, "Input");
            bool fromIn = group.Contains(TypeOf(fromAim)), toIn = group.Contains(TypeOf(toAim));
            if (fromIn == toIn) continue;                              // inside the group, or not touching it
            var types = Stated(line, "Output") ?? Stated(line, "Input") ??
                        labels.GetValueOrDefault(fromLabel) ?? labels.GetValueOrDefault(toLabel);
            if (types is null) return false;                           // a line the parent does not type
            var direction = toIn ? "Input" : "Output";
            if (!exposed.Any(e => e.Direction == direction && e.Types.SetEquals(types))) exposed.Add((direction, types));
        }

        var own = Ports(combined);
        bool Covered(string d, HashSet<string> t, IEnumerable<(string Direction, HashSet<string> Types)> by) =>
            by.Any(b => b.Direction == d && b.Types.Overlaps(t));
        return exposed.All(e => Covered(e.Direction, e.Types, own.Select(p => (p.Direction, p.Types)))) &&
               own.All(p => Covered(p.Direction, p.Types, exposed));
    }

    private static (string Aim, string Label) End(JsonElement line, string side)
    {
        if (!line.TryGetProperty(side, out var end)) return ("", "");
        return (end.TryGetProperty("AIMName", out var a) ? a.GetString() ?? "" : "",
                end.TryGetProperty("PortName", out var p) ? p.GetString() ?? "" : "");
    }

    // The Data Type an end states itself, if it does; its name is then not read.
    private static HashSet<string>? Stated(JsonElement line, string side) =>
        line.TryGetProperty(side, out var end) && end.TryGetProperty("DataType", out var t) && t.ValueKind == JsonValueKind.String
            ? new HashSet<string>(StringComparer.Ordinal) { t.GetString() ?? "" }
            : null;

    private static HashSet<string> TypesOf(JsonElement x)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        if (x.TryGetProperty("DataType", out var t))
        {
            if (t.ValueKind == JsonValueKind.Array) foreach (var y in t.EnumerateArray()) types.Add(y.GetString() ?? "");
            else types.Add(t.GetString() ?? "");
        }
        return types;
    }

    private bool Contains(string instance, HashSet<string> types, int depth)
    {
        if (depth > 16 || findL3(instance) is not { } l3) return false;
        foreach (var sub in SubAims(l3))
            if (types.Contains(TypeOf(sub)) || Contains(sub, types, depth + 1)) return true;
        return false;
    }

    private static IEnumerable<string> SubAims(JsonElement aim)
    {
        if (!aim.TryGetProperty("SubAIMs", out var subs) || subs.ValueKind != JsonValueKind.Array) yield break;
        foreach (var s in subs.EnumerateArray())
            if (s.TryGetProperty("Identifier", out var i) && i.TryGetProperty("AIMName", out var n) && n.GetString() is { Length: > 0 } name)
                yield return name;
    }

    // Name is only shown to the person reading the report; nothing matches on it.
    private sealed record Port(string Direction, HashSet<string> Types, string Name, bool Optional, int Number,
                               string Technology, bool IsRemote)
    {
        public override string ToString() => $"{Direction} {string.Join("|", Types.Order())} ({Name})";
    }

    private static List<Port> Ports(JsonElement aim)
    {
        var ports = new List<Port>();
        if (!aim.TryGetProperty("ExternalPorts", out var array) || array.ValueKind != JsonValueKind.Array) return ports;
        foreach (var p in array.EnumerateArray())
        {
            var types = new HashSet<string>(StringComparer.Ordinal);
            if (p.TryGetProperty("DataType", out var t))
            {
                if (t.ValueKind == JsonValueKind.Array) foreach (var x in t.EnumerateArray()) types.Add(x.GetString() ?? "");
                else types.Add(t.GetString() ?? "");
            }
            ports.Add(new Port(
                p.TryGetProperty("Direction", out var d) ? d.GetString() ?? "" : "",
                types,
                p.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "",
                p.TryGetProperty("IsOptional", out var o) && o.ValueKind == JsonValueKind.True,
                p.TryGetProperty("PortNumber", out var pn) && pn.ValueKind == JsonValueKind.Number ? pn.GetInt32() : 1,
                // "" is the default, Software; a placeholder is the implementer's choice of it.
                p.TryGetProperty("Technology", out var te) && te.GetString() is { Length: > 0 } tech && !IsPlaceholder(tech) ? tech : "Software",
                // IsRemote true (or "True") permits, or states, a remote Port; "" or false does not.
                p.TryGetProperty("IsRemote", out var ir) && (ir.ValueKind == JsonValueKind.True ||
                                                             ir.ValueKind == JsonValueKind.String && ir.GetString() == "True")));
        }
        return ports;
    }
}
