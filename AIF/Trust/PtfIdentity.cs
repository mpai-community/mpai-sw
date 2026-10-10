using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIF.Trust;

// THE CRYPTOGRAPHIC INSTANCE IDENTITY of a Process Instance (MPAI-PTF V1.0,
// PTF-CII): its public key, what it is, and proof that whoever presents it holds
// the private key - the Integrity: the fingerprint of the key, and a signature of
// the whole identity by that same key. A CII says who holds a key; it is the
// credential, issued by someone else, that says the holder is to be trusted.
//
// The key is an ECDSA P-256 key, written as its SubjectPublicKeyInfo (spki) in
// hexadecimal; the fingerprint is SHA-256 of those bytes.
public static class PtfIdentity
{
    public const string Header = "PTF-CII-V1.0";
    public const string Integrity = "Integrity";
    public const string SignatureAlgorithm = "PTF-ALGO-SIG-ECDSA-P256-SHA256";
    public const string HashAlgorithm = "PTF-ALGO-HASH-SHA256";

    public enum Outcome { Valid, Malformed, FingerprintMismatch, NotPossessed }

    // The CII of the instance whose key this is, signed by it. instanceId names the
    // instance (an AIM Instance: <Module instance>/<AIM>); implementation, what it
    // runs, where known.
    public static JsonObject Make(ECDsa key, string instanceId, string? implementation, DateTimeOffset now,
                                  string instanceType = "software")
    {
        var spki = key.ExportSubjectPublicKeyInfo();
        var attributes = new JsonObject { ["InstanceType"] = instanceType };
        if (implementation is { Length: > 0 }) attributes["Implementation"] = implementation;
        var cii = new JsonObject
        {
            ["Header"] = Header,
            ["CryptographicInstanceID"] = instanceId,
            ["CryptographicInstanceTime"] = Time(now),
            ["CryptographicBinding"] = new JsonObject
            {
                ["PublicKey"] = new JsonObject { ["Algorithm"] = SignatureAlgorithm, ["KeyEncoding"] = "spki", ["KeyValue"] = Convert.ToHexString(spki) },
                ["KeyDerivation"] = new JsonObject { ["Method"] = "direct" }
            },
            ["InstanceAttributes"] = attributes,
            [Integrity] = new JsonObject
            {
                ["Fingerprint"] = new JsonObject { ["Algorithm"] = HashAlgorithm, ["Value"] = Convert.ToHexString(SHA256.HashData(spki)) }
            }
        };
        return PtfSignature.Sign(cii, key, instanceId, Integrity);
    }

    // What a CII says, checked against itself: its key readable, the fingerprint
    // that key's, and the signature by that key - so that whoever presents it holds
    // the private key. The key, where it is valid.
    public static Outcome Check(JsonObject cii, out ECDsa? publicKey)
    {
        publicKey = PublicKey(cii);
        if (publicKey is null) return Outcome.Malformed;
        var spki = publicKey.ExportSubjectPublicKeyInfo();
        if (!string.Equals((string?)cii[Integrity]?["Fingerprint"]?["Value"], Convert.ToHexString(SHA256.HashData(spki)), StringComparison.OrdinalIgnoreCase))
            return Outcome.FingerprintMismatch;
        var key = publicKey;
        return PtfSignature.Verify(cii, _ => key, Integrity) == PtfSignature.Outcome.Valid ? Outcome.Valid : Outcome.NotPossessed;
    }

    public static ECDsa? PublicKey(JsonObject cii)
    {
        var binding = cii["CryptographicBinding"]?["PublicKey"];
        if ((string?)binding?["Algorithm"] != SignatureAlgorithm || (string?)binding?["KeyEncoding"] != "spki") return null;
        try
        {
            var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromHexString((string?)binding?["KeyValue"] ?? ""), out _);
            return key;
        }
        catch (Exception e) when (e is FormatException or CryptographicException) { return null; }
    }

    // The hash by which a credential names the CII it is for: SHA-256 of its
    // canonical form.
    public static string Hash(JsonObject cii) => Convert.ToHexString(SHA256.HashData(PtfCanonical.Bytes(cii)));

    public static string Time(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    // The AIM Type an AIM Instance implements: 1MMC-ASR-V2.5-I01 -> MMC-ASR-V2.5.
    public static string AimType(string aimInstance) =>
        Regex.Replace(Regex.Replace(aimInstance, @"^[0-9A-Za-z]*?(?=[A-Z]{3}-[A-Z]{3}-V)", ""), @"-I[0-9]+$", "");
}
