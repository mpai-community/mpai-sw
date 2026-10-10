using Json.Schema;

namespace AIF.Metadata;

// THE PUBLISHED SCHEMAS OF A PROCESS, loaded once. Each is registered under its
// absolute $id, so that the $refs between them resolve without the network; the
// library keeps one registry for the process and refuses a second registration
// of an $id - and a file loaded again after its registration. So whoever checks
// against the schemas - the AIM Metadata, the data of the Ports - takes them from
// here, and evaluates under the one lock: the library resolves references into
// the registry it shares, and two evaluations at once corrupt it.
public static class PublishedSchemas
{
    public static readonly object Lock = new();

    private static string? loadedFrom;
    private static Dictionary<string, JsonSchema>? byFile;

    // Every schema under root, by its full path. One process, one schemas folder.
    public static IReadOnlyDictionary<string, JsonSchema> At(string root)
    {
        root = Path.GetFullPath(root);
        lock (Lock)
        {
            if (byFile is null)
            {
                byFile = new Dictionary<string, JsonSchema>(StringComparer.OrdinalIgnoreCase);
                loadedFrom = root;
                foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        var s = JsonSchema.FromFile(file);
                        byFile[Path.GetFullPath(file)] = s;
                        if (s.BaseUri is { } id) SchemaRegistry.Global.Register(id, s);
                    }
                    catch { /* a file that is not a schema, or not valid: its own check will say so */ }
                }
            }
            else if (!string.Equals(loadedFrom, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"The schemas of {loadedFrom} are loaded; one process checks against one schemas folder, not also {root}.");
            return byFile;
        }
    }
}
