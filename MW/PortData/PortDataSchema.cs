using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Json.Schema;

namespace Mpai.Mas.PortData;

// IS WHAT CROSSES THE WIRE WHAT THE SCHEMA SAYS IT IS?
//
// The Port-data serialisers convert between the internal representation and the
// published schema instance, and until now nothing checked the second half of
// that claim. The schemas are the standard; the serialisers are one reading of
// them; and a schema revised without the code following would produce data that
// looks right and is not.
//
// THE INTERNAL FORM IS NOT THE SCHEMA FORM, which is why this validates the wire
// and not the Objects. A Speech Qualifier is Format in the code and Formats on
// the wire; the time is a SpaceTime in the code and a SimpleTime on the wire.
// Validating the in-process representation would fail everywhere and prove
// nothing. The wire is where a schema instance genuinely exists, and it is also
// where the standard's claim about interoperability applies - data passing
// between Implementations rather than within one.
//
// IT REPORTS AND DOES NOT REFUSE. A Module stopped because a schema was revised
// would be worse than one that says so.
public static class PortDataSchema
{
    // Where the published schemas live. Their $refs are absolute
    // https://schemas.mpai.community/... URLs, mapped here to the local copy.
    private const string Authority = "https://schemas.mpai.community/";

    private static string? root;
    private static bool    searched;

    // Data Type -> schema file, relative to the schemas root.
    private static readonly ConcurrentDictionary<string, string> Files = new()
    {
        ["OSD-BSO-V1.5"] = "OSD/V1.5/data/BasicSpeechObject.json",
        ["OSD-BTO-V1.5"] = "OSD/V1.5/data/BasicTextObject.json",
        ["OSD-BVO-V1.5"] = "OSD/V1.5/data/BasicVisualObject.json",
        ["OSD-STM-V1.5"] = "OSD/V1.5/data/SimpleTime.json",
        ["OSD-SEL-V1.5"] = "OSD/V1.5/data/Selector.json",
        ["MMC-SUM-V2.5"] = "MMC/V2.5/data/Summary.json",
        ["PAF-FDO-V1.6"] = "PAF/V1.6/data/FaceDescriptorsObject.json"
    };

    private static readonly ConcurrentDictionary<string, JsonSchema?> Loaded = new();

    // (Data Type, direction, complaint) - installed by a host that wants to hear.
    public static Action<string, string, string>? Sink { get; set; }

    // Where the schemas are. Found beside the application root if not set.
    public static string? Root
    {
        get
        {
            if (!searched)
            {
                searched = true;
                root ??= FindSchemas();
            }
            return root;
        }
        set { root = value; searched = true; }
    }

    private static string? FindSchemas()
    {
        var dir = AppContext.BaseDirectory;
        for (int up = 0; up < 8 && dir is not null; up++)
        {
            var candidate = Path.Combine(dir, "schemas");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    public static void Check(
        string dataType,
        string direction,
        byte[] wire)
    {
        if (Sink is null) return;

        var schema = SchemaFor(dataType);
        if (schema is null) return;

        try
        {
            using var document = JsonDocument.Parse(wire);
            var result   = schema.Evaluate(document.RootElement, new EvaluationOptions
            {
                OutputFormat = OutputFormat.List
            });

            if (result.IsValid) return;

            var said = new StringBuilder();
            said.Append("does not validate against ").Append(Files[dataType]).Append(':');
            Describe(result, said, 0);
            Sink.Invoke(dataType, direction, said.ToString());
        }
        catch (Exception ex)
        {
            Sink.Invoke(dataType, direction, "could not be validated: " + ex.Message);
        }
    }

    private static void Describe(EvaluationResults r, StringBuilder into, int depth)
    {
        if (depth > 3) return;
        if (r.Errors is not null)
            foreach (var e in r.Errors)
                into.Append(' ').Append(r.InstanceLocation).Append(' ').Append(e.Value).Append(';');
        foreach (var child in r.Details)
            if (!child.IsValid) Describe(child, into, depth + 1);
    }

    private static JsonSchema? SchemaFor(string dataType) =>
        Loaded.GetOrAdd(dataType, dt =>
        {
            if (Root is null || !Files.TryGetValue(dt, out var relative)) return null;

            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return null;

            // Register every published schema under its absolute $id, so that the
            // $refs between them resolve without reaching the network.
            foreach (var file in Directory.EnumerateFiles(Root!, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var s = JsonSchema.FromFile(file);
                    if (s.BaseUri is { } id) SchemaRegistry.Global.Register(id, s);
                }
                catch { /* a schema that will not parse is a separate fault */ }
            }

            try { return JsonSchema.FromFile(path); } catch { return null; }
        });
}