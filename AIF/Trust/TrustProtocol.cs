using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AIF.Trust;

// THE TRUST PROTOCOL (MPAI-PTF V1.0, Trust Establishment Protocol; M3223 3.4), as a
// Controller and an AIM host run it when a link between them opens. Each end is a
// Trust Anchor - a Controller is one (the author, 2026/09/25), and so is a host -
// configured with the anchors it trusts: a host, the Controllers it serves; a
// Controller, the hosts it uses. There is no key shared between them.
//
// The Controller sends a TrustRequest, the host answers with a TrustResponse; each
// is signed by its sender's anchor key (KeyID the AnchorID) and checked by the other
// against the anchors it trusts: known, current, signed by that key, fresh. Each
// names the TLS certificate of the other end as its sender sees it - the request,
// the host's; the response, the Controller's - so a message is good only on the TLS
// link it was sent on, and the channel and the identity are the same: a party that
// relays it to a third end presents a certificate the message does not name.
//
// ATTESTED (M3223 3.5): an end with a root of trust presents, beside its message,
// its Trust Anchor object and its Attestation Evidence - the quote of its
// registers over the hash of the other end's certificate, which is the nonce the
// other chose, new for each link. An end that requires attestation refuses a
// message without it, or whose evidence does not prove what it says.
public sealed class TrustProtocol
{
    public const string Header = "PTF-MSG-V1.0";
    public const string Operation = "EstablishTrust";
    public const string CertificateTarget = "TLSCertificate";

    // How far a message's time may be from the receiver's.
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);

    private readonly TrustAnchorKey self;
    private readonly ECDsa key;
    private readonly IEnumerable<JsonObject> trusted;          // read at each check: the list may change
    private readonly Func<DateTimeOffset> now;
    private readonly IRootOfTrust? attestor;
    private readonly Attestation.Policy? requires;
    private readonly Action<string, string, string, string?>? record;

    // THE CREDENTIAL OF THIS END, where an authority admitted it (M3241 3.1: the Trust
    // Authority of a city): presented with this end's anchor in every message, so that
    // an end that does not know the anchor trusts it through the authority. And the
    // anchors of the other ends so trusted, by their identifiers.
    private readonly JsonObject? credential;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsonObject> credited = new();

    // record: each check of the other end, as a Trust Operation - its type, the type
    // and identifier of what was checked, and why it failed where it did - for the
    // Trace of this end (M3223 3.6).
    public TrustProtocol(TrustAnchorKey self, ECDsa key, IEnumerable<JsonObject> trusted, Func<DateTimeOffset>? now = null,
                         IRootOfTrust? attestor = null, Attestation.Policy? requires = null,
                         Action<string, string, string, string?>? record = null, JsonObject? credential = null)
    {
        this.record = record;
        this.credential = credential;
        this.self = self;
        this.key = key;
        this.trusted = trusted;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.attestor = attestor;
        this.requires = requires;
    }

    public string Id => self.AnchorId;

    // The hash by which a message names a certificate: SHA-256 of its DER encoding.
    public static string CertificateHash(byte[] der) => Convert.ToHexString(SHA256.HashData(der));

    // THE REQUEST: the Controller's, naming the certificate the host presented.
    public JsonObject Request(string hostCertificate) => Signed(Presented(new JsonObject
    {
        ["Header"] = Header,
        ["MessageType"] = "TrustRequest",
        ["MessageID"] = $"{Id}#MSG-{Guid.NewGuid():N}",
        ["MessageTime"] = Time(),
        ["RequesterID"] = Id,
        ["Request"] = new JsonObject { ["Operation"] = Operation, ["TargetType"] = CertificateTarget, ["TargetID"] = hostCertificate }
    }, hostCertificate));

    // This end's anchor and the evidence of what it runs, quoted over the other end's
    // certificate: where it has a root of trust.
    private JsonObject Presented(JsonObject message, string otherCertificate)
    {
        if (attestor is null && credential is null) return message;
        var presentation = new JsonObject { ["TrustAnchor"] = self.Object() };
        if (attestor is not null) presentation["AttestationEvidence"] = attestor.Evidence(Id, Convert.FromHexString(otherCertificate));
        if (credential is not null) presentation["Credential"] = credential.DeepClone();
        message["Presentation"] = presentation;
        return message;
    }

    // The anchor of an end this end trusts - given it, or trusted through an
    // authority's credential - by its identifier; null where it trusts none.
    public JsonObject? AnchorOf(string anchorId) =>
        trusted.FirstOrDefault(a => (string?)a["AnchorID"] == anchorId) ?? credited.GetValueOrDefault(anchorId);

    // THE ANSWER: the host's, to a request received on a link whose certificates are
    // these. Signed whether it admits the requester or not; the requester it admits,
    // or why not.
    public (JsonObject Response, string? Admitted, string? Refused) Answer(JsonObject request, string ownCertificate, string requesterCertificate)
    {
        var refused = Check(request, "TrustRequest", "RequesterID", (string?)request["Request"]?["TargetID"], ownCertificate);
        if (refused is null && (string?)request["Request"]?["Operation"] != Operation) refused = $"it asks for {request["Request"]?["Operation"]}, not {Operation}";
        var response = new JsonObject
        {
            ["Header"] = Header,
            ["MessageType"] = "TrustResponse",
            ["MessageID"] = $"{Id}#MSG-{Guid.NewGuid():N}",
            ["MessageTime"] = Time(),
            ["ResponderID"] = Id,
            ["Response"] = refused is null
                ? new JsonObject { ["Status"] = "Success", ["Result"] = requesterCertificate }
                : new JsonObject { ["Status"] = "Failure", ["Reason"] = refused, ["Result"] = requesterCertificate }
        };
        if (refused is null) Presented(response, requesterCertificate);
        return (Signed(response), refused is null ? (string)request["RequesterID"]! : null, refused);
    }

    // THE RESPONSE, checked by the requester: from a host it trusts, for this link,
    // and admitting it. Null where it is; otherwise why not.
    public string? Check(JsonObject response, string ownCertificate)
    {
        var refused = Check(response, "TrustResponse", "ResponderID", (string?)response["Response"]?["Result"], ownCertificate);
        if (refused is not null) return refused;
        if ((string?)response["Response"]?["Status"] == "Success") return null;

        // Refused by the other end, as its policy judged this end: recorded too.
        var reason = $"it did not admit this Controller: {response["Response"]?["Reason"]}";
        record?.Invoke("EvaluatePolicy", "TrustMessage", (string?)response["MessageID"] ?? "", reason);
        return reason;
    }

    // One message: well formed; signed by an anchor this end trusts, current, whose
    // key verifies it; its sender that anchor; fresh; and naming this end's
    // certificate.
    // Each check recorded: the message - its sender, signature, time and link - and,
    // where required, the evidence of what the sender runs.
    private string? Check(JsonObject message, string type, string senderField, string? namedCertificate, string ownCertificate)
    {
        var refused = CheckMessage(message, type, senderField, namedCertificate, ownCertificate, out var anchor);
        record?.Invoke("VerifySignature", "TrustMessage", (string?)message["MessageID"] ?? "", refused);
        if (refused is not null || requires is null) return refused;

        // What it runs: quoted over this end's certificate - the nonce this end chose
        // for the link.
        var evidence = message["Presentation"]?["AttestationEvidence"] as JsonObject;
        using var anchorKey = TrustAnchorKey.KeyOf(anchor!);
        var unproven = evidence is null ? "no evidence of what it runs"
            : Attestation.Check(evidence, anchorKey!, (string)anchor!["AnchorID"]!, Convert.FromHexString(ownCertificate), requires, now());
        record?.Invoke("ValidateEvidence", "AttestationEvidence", (string?)evidence?["AttestationEvidenceID"] ?? "", unproven);
        return unproven is null ? null : $"not attested: {unproven}";
    }

    private string? CheckMessage(JsonObject message, string type, string senderField, string? namedCertificate, string ownCertificate,
                                 out JsonObject? anchor)
    {
        anchor = null;
        if ((string?)message["Header"] != Header || (string?)message["MessageType"] != type) return $"not a {type}";
        var keyId = (string?)message["KeyID"] ?? "";
        anchor = trusted.FirstOrDefault(a => (string?)a["AnchorID"] == keyId);
        if (anchor is null)
        {
            anchor = Credited(message, keyId, out var why);
            if (anchor is null) return $"foreign: {keyId} is not an anchor this end trusts{why}";
        }
        var t = now();
        if (TrustAnchorKey.ValidityOf(anchor) is not var (notBefore, notAfter) || t < notBefore || t > notAfter)
            return $"expired: the anchor {keyId} is not valid now";
        using var anchorKey = TrustAnchorKey.KeyOf(anchor);
        var signed = PtfSignature.Verify(message, id => id == keyId ? anchorKey : null);
        if (signed != PtfSignature.Outcome.Valid) return $"forged: its signature is {signed} under the key of {keyId}";
        if ((string?)message[senderField] != keyId) return $"it names {message[senderField]} and is signed by {keyId}";
        if (!DateTimeOffset.TryParse((string?)message["MessageTime"]?["Data"], out var sent) || (t - sent).Duration() > Freshness)
            return $"stale: sent {(t - sent).Duration().TotalMinutes:0} minutes from its receipt";
        if (!string.Equals(namedCertificate, ownCertificate, StringComparison.OrdinalIgnoreCase))
            return "not for this link: it names another certificate";
        return null;
    }

    // AN ANCHOR THIS END DOES NOT KNOW, presented with a credential for it issued by an
    // anchor it trusts - the Trust Authority of a city: trusted, and remembered. Its
    // signature on the message, its time and link are checked as any other's.
    private JsonObject? Credited(JsonObject message, string keyId, out string why)
    {
        why = "";
        if (message["Presentation"]?["TrustAnchor"] is not JsonObject presented || message["Presentation"]?["Credential"] is not JsonObject credential)
            return null;
        if ((string?)presented["AnchorID"] != keyId) { why = ", and the anchor it presents is another's"; return null; }
        if ((string?)credential["Subject"]?["InstanceID"] != keyId) { why = ", and its credential is for another"; return null; }
        var outcome = PtfCredential.CheckCredential(credential, presented,
            id => trusted.FirstOrDefault(a => (string?)a["AnchorID"] == id) is { } issuer ? TrustAnchorKey.KeyOf(issuer) : null, now());
        record?.Invoke("VerifyCredential", "InstanceCredential", (string?)credential["InstanceCredentialID"] ?? "", outcome == PtfCredential.Outcome.Valid ? null : outcome.ToString());
        if (outcome != PtfCredential.Outcome.Valid) { why = $", and its credential is {outcome}"; return null; }
        credited[keyId] = presented;
        return presented;
    }

    private JsonObject Time() => new()
    {
        ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = $"{Id}#TIM-{Guid.NewGuid():N}", ["Data"] = PtfIdentity.Time(now())
    };

    private JsonObject Signed(JsonObject message) => PtfSignature.Sign(message, key, Id);
}
