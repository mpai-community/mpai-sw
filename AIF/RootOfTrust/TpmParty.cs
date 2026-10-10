using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

using AIF.Trust;

namespace AIF.RootOfTrust;

// A PARTY WHOSE IDENTITY ITS TPM HOLDS (M3223 3.4, 3.5): a Controller or an AIM host.
// Provisioned once - its TPM enrolled by the manufacturer, its Trust Anchor object
// made from the identity key the TPM holds - and kept in a file that holds no
// private key: the anchor, the certificate of the attestation key, and where the
// TPM is. Loaded at each start: the TPM booted, its identity key the party's anchor
// key.
public static class TpmParty
{
    public static void Provision(string path, string anchorId, string tpmHost, int tpmPort, SimulatedManufacturer manufacturer,
                                 DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var tpm = new SimulatedTpm(anchorId, tpmHost, tpmPort, anchorId);
        tpm.Enroll(manufacturer);
        File.WriteAllText(path, new JsonObject
        {
            ["Anchor"] = new TrustAnchorKey(anchorId, tpm.IdentityKey, notBefore, notAfter).Object(),
            ["AttestationKeyCertificate"] = Convert.ToHexString(tpm.AttestationKeyCertificate!),
            ["Tpm"] = $"{tpmHost}:{tpmPort}"
        }.ToJsonString());
    }

    public static bool Is(string path) => JsonNode.Parse(File.ReadAllText(path))?["Tpm"] is not null;

    public static (TrustAnchorKey Anchor, SimulatedTpm Tpm) Load(string path)
    {
        var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var anchor = saved["Anchor"]!.AsObject();
        var id = (string)anchor["AnchorID"]!;
        var at = (string)saved["Tpm"]!;
        var colon = at.LastIndexOf(':');
        var tpm = new SimulatedTpm(id, at[..colon], int.Parse(at[(colon + 1)..]), id);
        using var named = TrustAnchorKey.KeyOf(anchor);
        if (named is null || !named.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(tpm.IdentityKey.ExportSubjectPublicKeyInfo()))
        {
            tpm.Dispose();
            throw new InvalidDataException($"{path}: the TPM at {at} does not hold the key of the anchor {id}.");
        }
        tpm.UseCertificate(Convert.FromHexString((string)saved["AttestationKeyCertificate"]!));
        var (from, to) = TrustAnchorKey.ValidityOf(anchor)!.Value;
        return (new TrustAnchorKey(id, tpm.IdentityKey, from, to), tpm);
    }

    public static X509Certificate2 Manufacturer(string certificateFile) => X509CertificateLoader.LoadCertificateFromFile(certificateFile);
}
