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
    private static readonly Regex AimNamePattern =
        new(@"^[A-Z]{3}-[A-Z]{3}-V[0-9]+\.[0-9]+$");

    private readonly string folder;

    public MpaiStore(
        string folder)
    {
        this.folder = folder;

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

    // Admits an AIM Metadata instance to the store, if it is valid.
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

    // What an AIM Metadata instance must satisfy to be self-consistent.
    // The JSON Schema states the shape; these are the checks that need the
    // instance as a whole, and the store as context.
    public StoreResult Validate(
        string amdJson)
    {
        var errors =
            new List<string>();

        var warnings =
            new List<string>();

        JsonDocument document;

        try
        {
            document =
                JsonDocument.Parse(amdJson);
        }
        catch (JsonException failure)
        {
            return StoreResult.Rejected(
                "",
                new[]
                {
                    "Not valid JSON: " + failure.Message
                });
        }

        using (document)
        {
            var root =
                document.RootElement;

            var aimName = "";

            if (!root.TryGetProperty(
                    "Identifier",
                    out var identifier))
            {
                errors.Add("Identifier is missing.");
            }
            else if (!identifier.TryGetProperty(
                         "AIMName",
                         out var aimNameElement))
            {
                errors.Add("Identifier.AIMName is missing.");
            }
            else
            {
                aimName =
                    aimNameElement.GetString()
                    ?? "";

                if (!AimNamePattern.IsMatch(aimName))
                {
                    errors.Add(
                        $"'{aimName}' is not a well-formed AIM name " +
                        "(expected e.g. MMC-AMQ-V2.5).");
                }
            }

            foreach (var required in new[]
                     {
                         "APIProfile",
                         "Description",
                         "Types",
                         "Ports",
                         "SubAIMs",
                         "Topology",
                         "Implementations"
                     })
            {
                if (!root.TryGetProperty(required, out _))
                {
                    errors.Add($"{required} is missing.");
                }
            }

            var types =
                new HashSet<string>();

            if (root.TryGetProperty("Types", out var typeArray) &&
                typeArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var type in typeArray.EnumerateArray())
                {
                    var name =
                        Text(type, "Name");

                    if (name.Length == 0)
                    {
                        errors.Add("A Type has no Name.");
                    }
                    else if (!types.Add(name))
                    {
                        errors.Add($"Type {name} is declared twice.");
                    }
                }
            }

            var ports =
                new HashSet<string>();

            if (root.TryGetProperty("Ports", out var portArray) &&
                portArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var port in portArray.EnumerateArray())
                {
                    var name =
                        Text(port, "Name");

                    var direction =
                        Text(port, "Direction");

                    var recordType =
                        Text(port, "RecordType");

                    if (name.Length == 0)
                    {
                        errors.Add("A Port has no Name.");
                        continue;
                    }

                    if (!ports.Add(name))
                    {
                        errors.Add($"Port {name} is declared twice.");
                    }

                    if (direction != "InputOutput" &&
                        direction != "OutputInput")
                    {
                        errors.Add(
                            $"Port {name} has Direction '{direction}'; " +
                            "expected InputOutput or OutputInput.");
                    }

                    if (recordType.Length > 0 &&
                        !types.Contains(recordType))
                    {
                        errors.Add(
                            $"Port {name} uses RecordType {recordType}, " +
                            "which is not declared in Types.");
                    }
                }
            }

            var subAims =
                new HashSet<string>();

            if (root.TryGetProperty("SubAIMs", out var subArray) &&
                subArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var subAim in subArray.EnumerateArray())
                {
                    if (!subAim.TryGetProperty(
                            "Identifier",
                            out var subIdentifier))
                    {
                        errors.Add("A SubAIM has no Identifier.");
                        continue;
                    }

                    var subName =
                        Text(subIdentifier, "AIMName");

                    if (subName.Length == 0)
                    {
                        errors.Add(
                            "A SubAIM has no Identifier.AIMName.");
                        continue;
                    }

                    subAims.Add(subName);

                    if (!Exists(subName) &&
                        subName != aimName)
                    {
                        warnings.Add(
                            $"SubAIM {subName} is not in the store yet; " +
                            "publish it before running this AIM.");
                    }
                }
            }

            if (root.TryGetProperty("Topology", out var topology) &&
                topology.ValueKind == JsonValueKind.Array)
            {
                foreach (var connection in topology.EnumerateArray())
                {
                    CheckEndpoint(
                        connection,
                        "OutputInput",
                        subAims,
                        errors);

                    CheckEndpoint(
                        connection,
                        "InputOutput",
                        subAims,
                        errors);
                }
            }

            return errors.Count == 0
                ? StoreResult.Valid(
                    aimName,
                    warnings)
                : StoreResult.Rejected(
                    aimName,
                    errors,
                    warnings);
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

    private static void CheckEndpoint(
        JsonElement connection,
        string side,
        ICollection<string> subAims,
        ICollection<string> errors)
    {
        if (!connection.TryGetProperty(side, out var endpoint))
        {
            errors.Add(
                $"A Topology connection has no {side}.");
            return;
        }

        var aimName =
            Text(endpoint, "AIMName");

        var portName =
            Text(endpoint, "PortName");

        if (portName.Length == 0)
        {
            errors.Add(
                $"A Topology {side} has no PortName.");
        }

        if (aimName.Length > 0 &&
            !subAims.Contains(aimName))
        {
            errors.Add(
                $"Topology {side} names {aimName}, " +
                "which is not one of the SubAIMs.");
        }
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