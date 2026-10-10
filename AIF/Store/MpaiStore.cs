using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIF.Store;

// The MPAI Store, as seen from inside the system.
//
// The AMD folder is not a place where files are simply dropped: an AIM enters
// the system by being PUBLISHED, and publishing means the Metadata was checked
// first. This service performs that check and, only if it passes, admits the
// AIM Metadata instance to the store.
//
// AmdStore remains the read path used by the Controller; this is the write path.
public sealed class MpaiStore
{
    private readonly string folder;

    // What the L3 does not conform to in its L2 (none when it conforms).
    private readonly Func<JsonElement, IReadOnlyList<string>>? conformance;

    public MpaiStore(
        string folder,
        Func<JsonElement, IReadOnlyList<string>>? conformance = null)
    {
        this.folder = folder;
        this.conformance = conformance;

        Directory.CreateDirectory(folder);
    }

    // ---- reading ----------------------------------------------------------

    public IReadOnlyList<string> List()
    {
        return Directory.EnumerateFiles(folder, "*.json")
                        .Select(Path.GetFileNameWithoutExtension)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Select(name => name!)
                        .OrderBy(name => name)
                        .ToList();
    }

    // Compatibility API used by StoreApp.
    // Returns true if at least one implementation of the AIM exists.
    public bool Exists(
        string aimName)
    {
        return FindByAimName(aimName) is not null;
    }

    // Compatibility API used by StoreApp.
    // Returns the first implementation found for the AIM.
    public string Retrieve(
        string aimName)
    {
        var identifier =
            FindByAimName(aimName);

        if (identifier is null)
        {
            throw new FileNotFoundException(
                $"{aimName} is not in the store.");
        }

        return File.ReadAllText(
            PathOf(identifier));
    }

    // New identifier-based API.
    public bool Exists(
        Identifier identifier)
    {
        return File.Exists(
            PathOf(identifier));
    }

    // New identifier-based API.
    public string Retrieve(
        Identifier identifier)
    {
        var path =
            PathOf(identifier);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"{identifier} is not in the store.",
                path);
        }

        return File.ReadAllText(path);
    }

    // ---- publishing -------------------------------------------------------

    // APPROVES an L3 the Store holds, with the binaries of its Implementations
    // (M3223 3.2): records the fingerprint of each binary it names, as submitted. A
    // binary the L3 does not name is not recorded; one it names and is not submitted
    // is reported. Approval does not publish: publishing is Publish's, with its own
    // check of the Metadata.
    public StoreResult Approve(string aimName, IReadOnlyDictionary<string, string> binaries)
    {
        var identifier = FindByAimName(aimName);
        if (identifier is null)
            return StoreResult.Rejected(aimName, new[] { $"{aimName} is not in the store." });
        using var document = JsonDocument.Parse(File.ReadAllText(PathOf(identifier)));
        var named = document.RootElement.TryGetProperty("Implementations", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(i => i.TryGetProperty("BinaryName", out var b) ? b.GetString() : null).OfType<string>().ToList()
            : new List<string>();
        var fingerprints = new ImplementationFingerprints(folder);
        var reported = new List<string>();
        foreach (var binary in named)
            if (binaries.TryGetValue(binary, out var file)) fingerprints.Approve(aimName, binary, ImplementationFingerprints.Of(file), DateTimeOffset.UtcNow);
            else reported.Add($"{binary}: named by the L3, not submitted - no fingerprint recorded");
        if (named.Count == 0) reported.Add("the L3 names no binary");
        return StoreResult.Valid(identifier.ToString(), reported);
    }

    public StoreResult Publish(
        string amdJson,
        bool replace = false)
    {
        var result =
            Validate(amdJson);

        if (!result.IsValid)
        {
            return result;
        }

        var identifier =
            ExtractIdentifier(amdJson);

        var path =
            PathOf(identifier);

        if (File.Exists(path) &&
            !replace)
        {
            return StoreResult.Rejected(
                identifier.ToString(),
                new[]
                {
                    $"{identifier} is already published. " +
                    "Publish a new version, or replace it deliberately."
                });
        }

        File.WriteAllText(
            path,
            amdJson);

        return StoreResult.Published(
            identifier.ToString(),
            path,
            result.Warnings);
    }

    public StoreResult PublishFile(
        string amdFile,
        bool replace = false)
    {
        if (!File.Exists(amdFile))
        {
            return StoreResult.Rejected(
                "",
                new[]
                {
                    $"File not found: {amdFile}"
                });
        }

        return Publish(
            File.ReadAllText(amdFile),
            replace);
    }

    // ---- validation -------------------------------------------------------

    // AN L3 IS VALIDATED AGAINST ITS L2, not against the AIM Metadata schema (L1):
    // the L2 of its type is what the Standard allows, and the L2 itself is valid
    // against L1. The check is the host's (AIF.Metadata.L2Conformance, given to the
    // constructor), so that AIF.Store carries no validator; without it nothing is
    // published. Before it, what import will do to the L3: its names resolved to
    // Data Types (TopologyNormaliser) - an L3 it refuses is refused here.
    public StoreResult Validate(
        string amdJson)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        JsonDocument document;
        try { document = JsonDocument.Parse(amdJson); }
        catch (JsonException failure) { return StoreResult.Rejected("", new[] { "Not valid JSON: " + failure.Message }); }

        using (document)
        {
            var root = document.RootElement;
            var aimName = root.TryGetProperty("Identifier", out var identifier) ? Text(identifier, "AIMName") : "";
            if (aimName.Length == 0)
                return StoreResult.Rejected("", new[] { "Identifier.AIMName is missing: without it the Store cannot say what the L3 is." });

            try { TopologyNormaliser.Normalise(root).Dispose(); }
            catch (InvalidOperationException refusal) { errors.Add(refusal.Message); }

            if (conformance is null)
                errors.Add("This Store has no L2 to check an L3 against: nothing is published without that check.");
            else
                errors.AddRange(conformance(root));

            if (root.TryGetProperty("SubAIMs", out var subAims) && subAims.ValueKind == JsonValueKind.Array)
                foreach (var subAim in subAims.EnumerateArray())
                    if (subAim.TryGetProperty("Identifier", out var subIdentifier) &&
                        Text(subIdentifier, "AIMName") is { Length: > 0 } subName && subName != aimName && !Exists(subName))
                        warnings.Add($"SubAIM {subName} is not in the store yet; publish it before running this AIM.");

            return errors.Count == 0
                ? StoreResult.Valid(aimName, warnings)
                : StoreResult.Rejected(aimName, errors, warnings);
        }
    }

    private Identifier? FindByAimName(
        string aimName)
    {
        var scanner =
            new AmdRepositoryScanner(folder);

        foreach (var file in scanner.Scan())
        {
            using var document =
                JsonDocument.Parse(
                    File.ReadAllText(file));

            if (!document.RootElement.TryGetProperty(
                    "Identifier",
                    out var identifier))
            {
                continue;
            }

            var candidate =
                Text(
                    identifier,
                    "AIMName");

            if (candidate != aimName)
            {
                continue;
            }

            return new Identifier
            {
                ImplementerID =
                    Text(
                        identifier,
                        "ImplementerID"),

                ImplementationID =
                    Text(
                        identifier,
                        "ImplementationID"),

                AIMName =
                    candidate
            };
        }

        return null;
    }

    private static Identifier ExtractIdentifier(
        string amdJson)
    {
        using var document =
            JsonDocument.Parse(amdJson);

        var identifier =
            document.RootElement
                    .GetProperty("Identifier");

        return new Identifier
        {
            ImplementerID =
                Text(
                    identifier,
                    "ImplementerID"),

            ImplementationID =
                Text(
                    identifier,
                    "ImplementationID"),

            AIMName =
                Text(
                    identifier,
                    "AIMName")
        };
    }

    private static string Text(
        JsonElement element,
        string property)
    {
        return element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private string PathOf(
        Identifier identifier)
    {
        return Path.Combine(
            folder,
            identifier + ".json");
    }
}

// The outcome of validating or publishing an AIM Metadata instance.
public sealed class StoreResult
{
    public bool IsValid { get; init; }

    public bool WasPublished { get; init; }

    public string AimName { get; init; } = "";

    public string Path { get; init; } = "";

    public IReadOnlyList<string> Errors { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } =
        Array.Empty<string>();

    public static StoreResult Valid(
        string aimName,
        IReadOnlyList<string> warnings)
    {
        return new StoreResult
        {
            IsValid = true,
            AimName = aimName,
            Warnings = warnings
        };
    }

    public static StoreResult Published(
        string aimName,
        string path,
        IReadOnlyList<string> warnings)
    {
        return new StoreResult
        {
            IsValid = true,
            WasPublished = true,
            AimName = aimName,
            Path = path,
            Warnings = warnings
        };
    }

    public static StoreResult Rejected(
        string aimName,
        IReadOnlyList<string> errors,
        IReadOnlyList<string>? warnings = null)
    {
        return new StoreResult
        {
            IsValid = false,
            AimName = aimName,
            Errors = errors,
            Warnings = warnings ?? Array.Empty<string>()
        };
    }
}