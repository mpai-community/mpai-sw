using System.Text.Json;

namespace AIF.Store;

public sealed class AmdStore
{
    private readonly string repositoryPath;

    private readonly Dictionary<Identifier, JsonDocument>
        amdDocuments = new();

    public AmdStore(string repositoryPath)
    {
        this.repositoryPath = repositoryPath;
    }

    public int Count => amdDocuments.Count;

    public void Scan()
    {
        amdDocuments.Clear();

        var scanner = new AmdRepositoryScanner(repositoryPath);

        foreach (var file in scanner.Scan())
        {
            var json     = File.ReadAllText(file);
            var document = JsonDocument.Parse(json);

            // Skip files that are not AMDs (e.g. data-type schemas that
            // happen to live in the same folder). An AMD has an "Identifier".
            if (!document.RootElement.TryGetProperty("Identifier", out var identifierJson))
                continue;

            if (!identifierJson.TryGetProperty("AIMName", out _))
                continue;

            var identifier = new Identifier
            {
                ImplementerID    = identifierJson.TryGetProperty("ImplementerID", out var imp)    ? imp.GetString()  ?? string.Empty : string.Empty,
                ImplementationID = identifierJson.TryGetProperty("ImplementationID", out var impl) ? impl.GetString() ?? string.Empty : string.Empty,
                AIMName          = identifierJson.GetProperty("AIMName").GetString()               ?? string.Empty
            };

            amdDocuments[identifier] = document;
        }
    }

    public bool Exists(Identifier identifier)
    {
        return amdDocuments.ContainsKey(identifier);
    }

    // Look up the AMD whose AIMName matches, regardless of ImplementerID /
    // ImplementationID. This is needed because SubAIM references in composite
    // AMDs may use placeholder strings for those fields that differ from the
    // actual AMD file's Identifier.
    public Identifier? FindByAimName(string aimName)
    {
        foreach (var key in amdDocuments.Keys)
        {
            if (key.AIMName == aimName)
                return key;
        }
        return null;
    }

    public IReadOnlyList<string> GetAimNames()
    {
        return amdDocuments.Keys
                           .Select(x => x.AIMName)
                           .Distinct()
                           .OrderBy(x => x)
                           .ToList();
    }

    public IReadOnlyList<CatalogItem> GetCatalog()
    {
        var catalog = new List<CatalogItem>();

        foreach (var pair in amdDocuments)
        {
            var root = pair.Value.RootElement;
            var id   = pair.Key;

            catalog.Add(new CatalogItem
            {
                AIMName          = id.AIMName,
                ImplementerID    = id.ImplementerID,
                ImplementationID = id.ImplementationID,
                Description      = root.GetProperty("Description").GetString() ?? string.Empty
            });
        }

        return catalog.OrderBy(c => c.AIMName).ToList();
    }

    public JsonDocument GetAMD(Identifier identifier)
    {
        if (!amdDocuments.TryGetValue(identifier, out var document))
            throw new InvalidOperationException($"AMD not found: {identifier}");
        return document;
    }
}
