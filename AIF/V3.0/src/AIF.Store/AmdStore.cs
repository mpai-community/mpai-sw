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

    // Look up by the identifier an L3 uses to name an AIM. Every AIM in an L3 is
    // an implementation instance - 1MMC-ASR-V2.5-I01, not MMC-ASR-V2.5 - so this
    // match is exact, and a request for an implementation this Service does not
    // hold finds nothing rather than finding a different one.
    //
    // An L3 that named the standard AIM would be a diagram: it would say what the
    // Module is made of in principle, and leave open which software builds it.
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
