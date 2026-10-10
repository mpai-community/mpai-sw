using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIF.Store;

// THE FINGERPRINTS OF APPROVED IMPLEMENTATIONS (Phase 13, M3223 3.2; OI-9). When
// the Store approves an L3 it records, for each Implementation the L3 names by its
// BinaryName, the SHA-256 of that binary as the Implementer submitted it. A
// Controller that verifies what it runs compares the binary it is about to run with
// this record: the Store is the guarantor that an Implementation is what its
// Implementer built (M3205 3.7).
//
// Kept beside the L3s, in approved/<AIMName>.json - the Store's record, not the
// Implementer's Metadata.
public sealed class ImplementationFingerprints
{
    public const string Folder = "approved";
    private readonly string folder;

    public ImplementationFingerprints(string storeFolder) => folder = Path.Combine(storeFolder, Folder);

    // The fingerprint of a file: SHA-256, hexadecimal, upper case.
    public static string Of(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    // Records the fingerprint of the binary that implements an AIM, as approved.
    public void Approve(string aimName, string binaryName, string fingerprint, DateTimeOffset at)
    {
        Directory.CreateDirectory(folder);
        var path = PathOf(aimName);
        var record = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject()
                                       : new JsonObject { ["AIMName"] = aimName, ["Implementations"] = new JsonObject() };
        record["Implementations"]![binaryName] = new JsonObject
        {
            ["Algorithm"] = "SHA-256", ["Fingerprint"] = fingerprint, ["Approved"] = at.ToString("O")
        };
        File.WriteAllText(path, record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // The approved fingerprint of the binary an AIM's L3 names; null where the Store
    // approved none.
    public string? Approved(string aimName, string binaryName)
    {
        var path = PathOf(aimName);
        if (!File.Exists(path)) return null;
        return (string?)JsonNode.Parse(File.ReadAllText(path))?["Implementations"]?[binaryName]?["Fingerprint"];
    }

    private string PathOf(string aimName) => Path.Combine(folder, aimName + ".json");
}
