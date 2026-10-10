using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIF.Store;

// NAMES ARE RESOLVED WHEN AN L3 IS IMPORTED, AND NOWHERE ELSE.
//
// A name in an AIM Metadata - an ExternalPort's Name, an InternalType, the
// PortName of a Topology end - is a label for the person reading it. Here, once,
// each Topology end is given the Data Type its name stands for, as the composite
// itself declares it (in its ExternalPorts or InternalTypes), with the Port
// Number that identifies the Port and the Output group of the flow; then every
// name is removed. What the Controller, the executors and the AIMs receive is the
// normalised document: they can route by Data Type and Port Number only, because
// there is nothing else in it.
//
// An end may state its Data Type itself; its name is then not read. A name the
// composite does not declare, two ends of different Data Types, or a boundary end
// that does not say which of several Ports it is, refuse the L3 here.
public static class TopologyNormaliser
{
    public static JsonDocument Normalise(JsonElement amd)
    {
        var root = JsonNode.Parse(amd.GetRawText())!.AsObject();
        var aim = (string?)root["Identifier"]?["AIMName"] ?? "";

        var ports = new List<Port>();
        foreach (var p in root["ExternalPorts"]?.AsArray() ?? [])
            ports.Add(new Port((string?)p!["Name"] ?? "", (string?)p["Direction"] ?? "", TypesOf(p["DataType"]),
                               IntOf(p["PortNumber"]), IntOf(p["Output"])));

        var internals = new Dictionary<string, (List<string> Types, int? Output)>(StringComparer.Ordinal);
        // An InternalType is found by its label when the line uses one, and by its Data Type; the label is optional.
        var unnamed = 0;
        foreach (var it in root["InternalTypes"]?.AsArray() ?? [])
        {
            var name = (string?)it!["Name"];
            internals[name is { Length: > 0 } ? name : "\u0000" + unnamed++] = (TypesOf(it["DataType"]), IntOf(it["Output"]));
        }

        if (root["Topology"] is JsonArray topology)
        {
            var lines = new JsonArray();
            foreach (var line in topology)
                lines.Add(Line(aim, line!, ports, internals));
            root["Topology"] = lines;
        }

        foreach (var p in root["ExternalPorts"]?.AsArray() ?? []) p!.AsObject().Remove("Name");
        root.Remove("InternalTypes");
        return JsonDocument.Parse(root.ToJsonString());
    }

    private sealed record Port(string Name, string Direction, List<string> Types, int? PortNumber, int? Output)
    {
        public bool Accepts(string dataType) => Types.Contains(dataType);
    }

    private sealed record End(string Aim, string Name, string? DataType, int? Cited)
    {
        public bool IsBoundary => Aim.Length == 0;
        public override string ToString() =>
            (IsBoundary ? "" : Aim + ".") + (Name.Length > 0 ? Name : DataType) + (Cited is int c ? ":" + c : "");

        public static End Read(JsonNode? end) => new(
            (string?)end?["AIMName"] ?? "",
            (string?)end?["PortName"] ?? "",
            end?["DataType"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? (string?)v : null,
            IntOf(end?["PortNumber"]));
    }

    // What an end's name (or stated Data Type) stands for: the Data Types, the
    // boundary Port's own Number, the Output group of the flow.
    private sealed record Declared(List<string> Types, int? PortNumber, int? Output)
    {
        public string DataType => Types.Count > 0 ? Types[0] : "";
        public bool Accepts(string dataType) => Types.Contains(dataType);
    }

    private static JsonObject Line(string aim, JsonNode line, List<Port> ports,
                                   Dictionary<string, (List<string> Types, int? Output)> internals)
    {
        var from = End.Read(line["Output"]);
        var to   = End.Read(line["Input"]);

        // A boundary end on the producing side is where data ENTERS the composite,
        // one of its Input ExternalPorts; on the receiving side, an Output one.
        var fromDecl = Declare(aim, from, "Input", ports, internals);
        var toDecl   = Declare(aim, to, "Output", ports, internals);

        if (!fromDecl.Accepts(toDecl.DataType) && !toDecl.Accepts(fromDecl.DataType))
            throw new InvalidOperationException(
                $"{aim}: Topology '{from}' -> '{to}' joins {fromDecl.DataType} to {toDecl.DataType}.");
        if (fromDecl.Output is int a && toDecl.Output is int b && a != b)
            throw new InvalidOperationException(
                $"{aim}: Topology '{from}' -> '{to}' is declared with Output {a} at one end and Output {b} at the other.");

        var dataType = fromDecl.DataType;
        var output = fromDecl.Output ?? toDecl.Output;

        var outputEnd = Typed(from, fromDecl, dataType);
        if (output is int n) outputEnd["Output"] = n;
        return new JsonObject { ["Output"] = outputEnd, ["Input"] = Typed(to, toDecl, dataType) };
    }

    // The boundary is numbered as the User Agent addresses it, by the ExternalPort's
    // own PortNumber (absent meaning 1); a Sub-AIM Port by the number the line cites.
    private static JsonObject Typed(End end, Declared declared, string dataType)
    {
        var typed = new JsonObject { ["AIMName"] = end.Aim, ["DataType"] = dataType };
        var number = end.IsBoundary ? declared.PortNumber ?? 1 : end.Cited;
        if (number is int n) typed["PortNumber"] = n;
        return typed;
    }

    private static Declared Declare(string aim, End end, string boundaryDirection, List<Port> ports,
                                    Dictionary<string, (List<string> Types, int? Output)> internals)
    {
        if (end.IsBoundary)
        {
            var named = ports.Where(p => p.Direction == boundaryDirection &&
                                         (end.DataType is not null ? p.Accepts(end.DataType) : p.Name == end.Name)).ToList();
            if (named.Count > 1 && end.Cited is null)
                throw new InvalidOperationException(
                    $"{aim}: Topology end '{end}' names {named.Count} {boundaryDirection} Ports; the line must state which by PortNumber.");
            var port = end.Cited is int c
                ? named.FirstOrDefault(p => (p.PortNumber ?? 1) == c) ?? (named.Count == 1 ? named[0] : null)
                : named.FirstOrDefault();
            if (port is null)
                throw new InvalidOperationException(
                    $"{aim}: Topology end '{end}' is on the boundary, and {aim} declares no {boundaryDirection} ExternalPort of that name and number.");

            // Without its name, a boundary Port is its Data Type and Number: two
            // Ports that agree on both could no longer be told apart.
            var number = port.PortNumber ?? 1;
            if (ports.Any(p => p != port && p.Direction == boundaryDirection && (p.PortNumber ?? 1) == number &&
                               p.Types.Intersect(port.Types).Any()))
                throw new InvalidOperationException(
                    $"{aim}: two {boundaryDirection} ExternalPorts of {port.Types[0]} are both Port {number}; number them.");
            return new Declared(port.Types, port.PortNumber, port.Output);
        }

        // A Sub-AIM end: the Data Type the end states, else the one the COMPOSITE
        // declares for its name. The name the Sub-AIM gives its own Port is never
        // read - nothing guarantees a sender and a receiver share it.
        // The composite declares the Output group of a Data Type in its InternalTypes (L1: "the Input group, of the
        // SubAIM this flow enters, that the flow feeds"), by Data Type: an end that states its Data Type finds it there,
        // when one group is declared for that Data Type.
        if (end.DataType is { } stated)
        {
            var groups = internals.Values.Where(v => v.Output is not null && v.Types.Contains(stated))
                                         .Select(v => v.Output).Distinct().ToList();
            return new Declared([stated], null, groups.Count == 1 ? groups[0] : null);
        }
        if (internals.TryGetValue(end.Name, out var it)) return new Declared(it.Types, null, it.Output);
        if (ports.FirstOrDefault(p => p.Name == end.Name) is { } external)
            return new Declared(external.Types, null, external.Output);
        throw new InvalidOperationException(end.Name.Length > 0
            ? $"{aim}: Topology end '{end}' uses the label '{end.Name}', which {aim} does not declare. " +
              "Declare it in its InternalTypes (or ExternalPorts), or state the end's DataType: a Sub-AIM's own Port names are not read."
            : $"{aim}: Topology end '{end}' states neither a label nor a DataType.");
    }

    private static List<string> TypesOf(JsonNode? dataType) => dataType switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.String => [(string)v!],
        JsonArray a => a.Select(x => (string?)x).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList(),
        _ => []
    };

    private static int? IntOf(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<int>(out var i) ? i : null;
}
