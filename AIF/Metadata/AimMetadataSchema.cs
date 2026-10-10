using System.Text.Json;

using Json.Schema;

namespace AIF.Metadata;

// AIM METADATA, AGAINST THE SCHEMA. An L3 (or an L2) is checked against the AIM
// Metadata schema of AIF V3.0 (AIF/V3.0/data/AIMMetadata.json), with every schema
// of the folder registered under its $id, so that the
// https://schemas.mpai.community/... references resolve locally and nothing is
// fetched from the network.
//
// What is wrong is reported once, as "location: what is wrong". Where anyOf or
// oneOf fails, the value matched none of its alternatives, and that is what is
// reported - not the failure of every alternative, which would turn one fault into
// a dozen. Elsewhere only the innermost failures are reported, not the "some
// properties did not match" of each enclosing object.
public sealed class AimMetadataSchema
{
    private readonly JsonSchema schema;

    private AimMetadataSchema(JsonSchema schema) => this.schema = schema;

    // The schema of the folder given, which holds AIF/V3.0/data/AIMMetadata.json.
    // Each file is built once in a process: building a schema registers its dynamic
    // anchors, and a second build of the same file collides with the first - so one
    // process uses one schemas folder.
    public static AimMetadataSchema Load(string schemasRoot)
    {
        var root = Path.GetFullPath(schemasRoot);
        var main = Path.Combine(root, "AIF", "V3.0", "data", "AIMMetadata.json");
        return PublishedSchemas.At(root).TryGetValue(main, out var schema)
            ? new AimMetadataSchema(schema)
            : throw new InvalidOperationException("The AIM Metadata schema could not be built: " + main);
    }

    // What is wrong with this AIM Metadata; none when it validates.
    public IReadOnlyList<string> Violations(JsonElement aim)
    {
        // ONE EVALUATION AT A TIME. The library resolves the schemas' references on
        // first use and records them in a dictionary it shares; two evaluations at
        // once corrupt it. A Store checking two submissions together, or two tests,
        // would fail at random.
        EvaluationResults evaluation;
        lock (PublishedSchemas.Lock)
            evaluation = schema.Evaluate(aim, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
        if (evaluation.IsValid) return [];
        var found = new SortedSet<string>(StringComparer.Ordinal);
        Walk(evaluation, found);
        return found.ToList();
    }

    public IReadOnlyList<string> Violations(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Violations(document.RootElement);
    }

    private static readonly HashSet<string> Summarising = new(StringComparer.Ordinal)
    {
        "properties", "patternProperties", "items", "prefixItems", "allOf", "$ref", "$dynamicRef",
        "dependentSchemas", "then", "else", "contains", "unevaluatedProperties", "unevaluatedItems"
    };

    private static void Walk(EvaluationResults node, SortedSet<string> found)
    {
        if (node.IsValid) return;
        var where = node.InstanceLocation.ToString() is { Length: > 0 } loc ? loc : "/";
        var keyword = node.EvaluationPath.ToString().Split('/').LastOrDefault() ?? "";

        var alternatives = keyword is "anyOf" or "oneOf" ? keyword
            : node.Errors?.Keys.FirstOrDefault(k => k is "anyOf" or "oneOf");
        if (alternatives is not null)
        {
            found.Add($"{where}: matches none of the alternatives ({alternatives})");
            return;
        }

        // A keyword that only summarises its subschemas ("some properties did not
        // match") says nothing its children do not say better; any other keyword
        // (required, type, pattern, const, enum, a false schema...) is a violation.
        var failing = (node.Details ?? []).Where(d => !d.IsValid).ToList();
        if (node.Errors is { Count: > 0 })
            foreach (var e in node.Errors)
                if (!Summarising.Contains(e.Key) || failing.Count == 0)
                    found.Add($"{where}: {e.Key} {Short(e.Value, 120)}");

        foreach (var child in failing) Walk(child, found);
    }

    private static string Short(string text, int max)
    {
        var one = text.Replace('\r', ' ').Replace('\n', ' ');
        return one.Length <= max ? one : one[..max] + "...";
    }
}
