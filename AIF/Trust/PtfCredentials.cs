using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AIF.Trust;

// CREDENTIALS (MPAI-PTF V1.0): the Instance Credential (PTF-ICR) by which an issuer
// binds a Cryptographic Instance Identity to an instance for a time, and the Process
// Lifecycle Credential (PTF-PLC) by which it states the instance's lifecycle state.
// Both signed by the issuer, whose KeyID they carry. In the AIF the instance is an
// AIM Instance, and the issuer a Controller - itself a Trust Anchor - or an AIM its
// credential entitles to issue.
public sealed class PtfIssuer
{
    public const string CredentialHeader = "PTF-ICR-V1.0";
    public const string LifecycleHeader = "PTF-PLC-V1.0";

    public string Name { get; }
    public string KeyId { get; }
    private readonly ECDsa key;

    public PtfIssuer(string name, ECDsa key, string keyId)
    {
        Name = name;
        this.key = key;
        KeyId = keyId;
    }

    // instanceType: AIMInstance in the AIF (ProcessInstance is MPAI-MMM's);
    // specification: what the instance is an instance of (for an AIM, its AIM Type);
    // scope: Issuer where the credential entitles its subject to issue credentials.
    public JsonObject IssueCredential(JsonObject cii, string instanceType, string instanceId, string? specification,
                                      DateTimeOffset now, TimeSpan lifetime, string? scope = null)
    {
        var subject = new JsonObject { ["InstanceType"] = instanceType, ["InstanceID"] = instanceId };
        if (specification is { Length: > 0 }) subject["Specification"] = specification;
        var validity = new JsonObject { ["NotBefore"] = PtfIdentity.Time(now), ["NotAfter"] = PtfIdentity.Time(now + lifetime) };
        if (scope is { Length: > 0 }) validity["Scope"] = scope;
        var credential = new JsonObject
        {
            ["Header"] = CredentialHeader,
            ["InstanceCredentialID"] = $"{instanceId}#ICR-{Guid.NewGuid():N}",
            ["Subject"] = subject,
            ["CII"] = new JsonObject { ["HashAlgorithm"] = PtfIdentity.HashAlgorithm, ["Hash"] = PtfIdentity.Hash(cii) },
            ["Issuer"] = new JsonObject { ["Name"] = Name, ["KeyID"] = KeyId },
            ["Validity"] = validity
        };
        return PtfSignature.Sign(credential, key, KeyId);
    }

    public JsonObject IssueLifecycle(string instanceId, string state, DateTimeOffset now, TimeSpan lifetime)
    {
        var lifecycle = new JsonObject
        {
            ["Header"] = LifecycleHeader,
            ["ProcessLifecycleCredentialID"] = $"{instanceId}#PLC-{Guid.NewGuid():N}",
            ["ProcessInstanceID"] = instanceId,
            ["LifecycleState"] = state,
            ["Issuer"] = new JsonObject { ["Name"] = Name, ["KeyID"] = KeyId },
            ["Validity"] = new JsonObject { ["NotBefore"] = PtfIdentity.Time(now), ["NotAfter"] = PtfIdentity.Time(now + lifetime) }
        };
        return PtfSignature.Sign(lifecycle, key, KeyId);
    }
}

// What a verifier checks of a credential, as the Verification Pipeline has it
// (MPAI-PTF V1.0, Trust Establishment Protocol 2): the issuer's signature, by a key
// the verifier trusts; the identity it is for; its validity at the time.
public static class PtfCredential
{
    // The Validity.Scope of a credential that entitles its subject to issue credentials
    // (proposed to PTF, which has no field for it).
    public const string IssuerScope = "Issuer";
    public const int MaxChain = 8;

    public enum Outcome
    {
        Valid, Malformed, UnknownIssuer, InvalidSignature, CiiMismatch, NotYetValid, Expired, WrongState,
        NotEntitled, ChainTooLong, ChainBroken
    }

    // One link of a CredentialChain: an issuer's CII and the credential it holds.
    public sealed record Link(JsonObject Cii, JsonObject Credential);

    public static Outcome CheckCredential(JsonObject credential, JsonObject cii, Func<string, ECDsa?> keys, DateTimeOffset now)
    {
        if ((string?)credential["Header"] != PtfIssuer.CredentialHeader) return Outcome.Malformed;
        var signed = Signed(credential, keys);
        if (signed != Outcome.Valid) return signed;
        if (!string.Equals((string?)credential["CII"]?["Hash"], PtfIdentity.Hash(cii), StringComparison.OrdinalIgnoreCase))
            return Outcome.CiiMismatch;
        return Current(credential, now);
    }

    // THE CREDENTIAL CHAIN (proposed to PTF, which names a CredentialChain in its
    // Trust Establishment Protocol, 2.3, and does not define it). A credential is
    // trusted when its issuer is a Trust Anchor the verifier trusts, or an issuer
    // whose own credential - a link of the chain presented with it - is trusted, and
    // entitles it to issue. Each link: the issuer's CII, proven by its possession of
    // the key; its credential, checked in turn. The chain ends at an anchor, or the
    // credential is not trusted.
    public static Outcome CheckChain(JsonObject credential, JsonObject cii, IReadOnlyList<Link> chain,
                                     Func<string, ECDsa?> anchors, DateTimeOffset now)
    {
        var (currentCredential, currentCii) = (credential, cii);
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 0; ; depth++)
        {
            if (depth > MaxChain) return Outcome.ChainTooLong;
            var issuerId = (string?)currentCredential["KeyID"] ?? "";
            if (anchors(issuerId) is not null) return CheckCredential(currentCredential, currentCii, anchors, now);

            // An issuer that is not an anchor: its link, found by the identity it
            // signs with, used once.
            var link = chain.FirstOrDefault(l => (string?)l.Cii["CryptographicInstanceID"] == issuerId);
            if (link is null || !used.Add(issuerId)) return link is null ? Outcome.UnknownIssuer : Outcome.ChainBroken;
            if (PtfIdentity.Check(link.Cii, out var issuerKey) != PtfIdentity.Outcome.Valid) return Outcome.ChainBroken;
            if ((string?)link.Credential["Subject"]?["InstanceID"] != issuerId) return Outcome.ChainBroken;
            if ((string?)link.Credential["Validity"]?["Scope"] != IssuerScope) return Outcome.NotEntitled;

            var checkedHere = CheckCredential(currentCredential, currentCii, id => id == issuerId ? issuerKey : null, now);
            if (checkedHere != Outcome.Valid) return checkedHere;
            (currentCredential, currentCii) = (link.Credential, link.Cii);
        }
    }

    public static Outcome CheckLifecycle(JsonObject lifecycle, string instanceId, string expectedState,
                                         Func<string, ECDsa?> keys, DateTimeOffset now)
    {
        if ((string?)lifecycle["Header"] != PtfIssuer.LifecycleHeader || (string?)lifecycle["ProcessInstanceID"] != instanceId)
            return Outcome.Malformed;
        var signed = Signed(lifecycle, keys);
        if (signed != Outcome.Valid) return signed;
        if ((string?)lifecycle["LifecycleState"] != expectedState) return Outcome.WrongState;
        return Current(lifecycle, now);
    }

    private static Outcome Signed(JsonObject obj, Func<string, ECDsa?> keys) =>
        PtfSignature.Verify(obj, keys) switch
        {
            PtfSignature.Outcome.Valid => Outcome.Valid,
            PtfSignature.Outcome.UnknownKey => Outcome.UnknownIssuer,
            PtfSignature.Outcome.Missing or PtfSignature.Outcome.Malformed => Outcome.Malformed,
            _ => Outcome.InvalidSignature
        };

    private static Outcome Current(JsonObject obj, DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParse((string?)obj["Validity"]?["NotBefore"], out var notBefore) ||
            !DateTimeOffset.TryParse((string?)obj["Validity"]?["NotAfter"], out var notAfter))
            return Outcome.Malformed;
        if (now < notBefore) return Outcome.NotYetValid;
        if (now > notAfter) return Outcome.Expired;
        return Outcome.Valid;
    }
}
