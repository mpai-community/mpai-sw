using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AIF.Trust;

// THE TRUST AUTHORITY OF A CITY (M3241 3.1; the author, 2026/09/28): upon entering the
// city a CAV is admitted and given its anchor - a key of its own - with a credential
// for it, which the Authority issues for a time. The CAVs of the city trust the
// Authority's anchor, and through its credentials each other (TrustProtocol).
public sealed class TrustAuthority
{
    public string City { get; }
    public TrustAnchorKey Anchor { get; }
    private readonly Func<DateTimeOffset> now;

    public TrustAuthority(string city, Func<DateTimeOffset>? now = null)
    {
        City = city;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        var t = this.now();
        Anchor = new TrustAnchorKey($"{city}-TrustAuthority", ECDsa.Create(ECCurve.NamedCurves.nistP256), t.AddHours(-1), t.AddDays(365));
    }

    // What a CAV of the city is given to trust the Authority.
    public JsonObject AnchorObject => Anchor.Object();

    // A CAV ADMITTED: its anchor and key, and the Authority's credential for the anchor
    // - its subject the CAV, for the time given (a day by default).
    public sealed record Admission(TrustAnchorKey Anchor, ECDsa Key, JsonObject Credential);

    public Admission Admit(string cavId, TimeSpan? lifetime = null, DateTimeOffset? from = null)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var t = from ?? now();
        var anchor = new TrustAnchorKey(cavId, key, t.AddMinutes(-5), t.AddDays(30));
        var credential = Anchor.Issuer.IssueCredential(anchor.Object(), "CAV", cavId, "MPAI-CAV", t, lifetime ?? TimeSpan.FromDays(1));
        return new Admission(anchor, key, credential);
    }

    // The CAV's end of the Trust Protocol: its anchor and key, its credential, the
    // Authority's anchor trusted.
    public TrustProtocol ProtocolFor(Admission admission, IEnumerable<JsonObject>? alsoTrusted = null) =>
        new(admission.Anchor, admission.Key, new[] { AnchorObject }.Concat(alsoTrusted ?? []).ToList(), now, credential: admission.Credential);
}

// A MESSAGE BETWEEN CAVs, SIGNED (M3241 3.3): in its DataXMData (MPAI-PTF Data Exchange
// Metadata), the signing CAV as its Source and a Security whose Identity names the
// Authority that admitted it and whose Integrity carries the hash of the Object, the
// signature over its canonical form without the signature, and the KeyID - the CAV's
// anchor. The Controller signs what its Module sends to another CAV, and verifies
// what it receives: only a Message signed by the CAV its link admitted is given on.
public static class ExternalSigning
{
    public enum Outcome { Valid, Unsigned, NotTheSender, UnknownKey, Tampered, Invalid }

    public static string Sign(string json, TrustAnchorKey anchor, ECDsa key, string authority, DateTimeOffset now)
    {
        var obj = JsonNode.Parse(json)!.AsObject();
        obj.Remove("DataXMData");
        var hash = Convert.ToHexString(SHA256.HashData(PtfCanonical.Bytes(obj)));
        var id = (string?)obj.FirstOrDefault(p => p.Key.EndsWith("ID", StringComparison.Ordinal) && p.Value is JsonValue).Value ?? Guid.NewGuid().ToString("N");
        obj["DataXMData"] = new JsonObject
        {
            ["Header"] = "PTF-DEM-V1.0", ["DataID"] = id,
            ["Source"] = new JsonArray(new JsonObject { ["AIMID"] = anchor.AnchorId }),
            ["Security"] = new JsonObject
            {
                ["Header"] = "PTF-SEC-V1.0",
                ["Identity"] = new JsonObject { ["Issuer"] = $"urn:mpai:ptf:authority:{authority}", ["CredentialRef"] = $"urn:mpai:ptf:cav:{anchor.AnchorId}" },
                ["Transmission"] = new JsonObject { ["Protocol"] = "Custom" },
                ["Integrity"] = new JsonObject { ["Hash"] = new JsonObject { ["Algorithm"] = "PTF-ALGO-HASH-SHA256", ["Value"] = hash }, ["KeyID"] = anchor.AnchorId }
            }
        };
        var signature = key.SignData(PtfCanonical.Bytes(obj), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        obj["DataXMData"]!["Security"]!["Integrity"]!["Signature"] = Convert.ToHexString(signature);
        return obj.ToJsonString();
    }

    // Signed by the CAV expected - the one the link admitted - whose anchor this end
    // trusts, over the Object as it came.
    public static Outcome Verify(string json, string expectedSigner, Func<string, JsonObject?> anchorOf)
    {
        var obj = JsonNode.Parse(json)!.AsObject();
        var integrity = obj["DataXMData"]?["Security"]?["Integrity"] as JsonObject;
        if ((string?)integrity?["Signature"] is not { } hex || (string?)integrity["KeyID"] is not { } keyId) return Outcome.Unsigned;
        if (keyId != expectedSigner) return Outcome.NotTheSender;
        if (anchorOf(keyId) is not { } anchor || TrustAnchorKey.KeyOf(anchor) is not { } key) return Outcome.UnknownKey;
        using (key)
        {
            var bare = obj.DeepClone().AsObject();
            bare.Remove("DataXMData");
            if (!string.Equals((string?)integrity["Hash"]?["Value"], Convert.ToHexString(SHA256.HashData(PtfCanonical.Bytes(bare))), StringComparison.OrdinalIgnoreCase))
                return Outcome.Tampered;
            var unsigned = obj.DeepClone().AsObject();
            unsigned["DataXMData"]!["Security"]!["Integrity"]!.AsObject().Remove("Signature");
            byte[] value;
            try { value = Convert.FromHexString(hex); } catch (FormatException) { return Outcome.Invalid; }
            return key.VerifyData(PtfCanonical.Bytes(unsigned), value, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                ? Outcome.Valid : Outcome.Invalid;
        }
    }
}
