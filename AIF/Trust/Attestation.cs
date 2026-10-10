using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace AIF.Trust;

// A ROOT OF TRUST, as a party - a Controller, an AIM host - has one (M3223 3.5): it
// holds the party's identity key and never releases it, measures the code the party
// runs into its registers, and quotes them, over a nonce its verifier chose, under
// an attestation key its manufacturer certified.
public interface IRootOfTrust
{
    // The party's identity key, held by the root of trust: it signs; it cannot be
    // exported.
    ECDsa IdentityKey { get; }

    // Extends a register with the SHA-256 of what is about to run, and logs it.
    void Measure(int register, string what, string sha256);

    // The party's Attestation Evidence (PTF-ATE) over a nonce its verifier chose: the
    // quote of its registers, the certificate of the key that signed it, and the log
    // of what was measured; signed by its identity key.
    JsonObject Evidence(string partyId, byte[] nonce);
}

// THE MEASUREMENTS, AND WHAT A VERIFIER DOES WITH THEM (M3223 3.5). A party's code is
// measured into register 10 before it runs; each Implementation it loads, into
// register 11. The Evidence carries the quote of both (AIF-EVID-TPM-QUOTE, proposed
// to PTF) - a TPM 2.0 TPMS_ATTEST and its
// signature - the attestation key's certificate, and each measurement. The verifier
// checks the signature of the Evidence, the certificate to the manufacturer it
// trusts, the quote's signature under the certified key, that the quote is over the
// nonce it chose, that the log replays to the registers quoted, and that every
// measurement is one it approves. Nothing here needs a TPM: any party can verify.
public static class Attestation
{
    public const int CodeRegister = 10;
    public const int ImplementationRegister = 11;
    public static readonly int[] Quoted = [CodeRegister, ImplementationRegister];

    // What a quote is, as the root of trust gives it.
    public sealed record Quote(byte[] Attest, byte[] Signature, byte[] AttestationKeyCertificate);

    public sealed record Measurement(int Register, string What, string Sha256);

    public const string ReportType = "AIF-EVID-TPM-QUOTE";
    public const string MeasurementType = "AIF-EVID-RUNTIME-MEASUREMENT";

    // THE EVIDENCE, signed by the party's identity key.
    public static JsonObject Evidence(string partyId, Quote quote, IEnumerable<Measurement> log, ECDsa identity)
    {
        var report = new JsonObject
        {
            ["Quote"] = Convert.ToHexString(quote.Attest),
            ["Signature"] = Convert.ToHexString(quote.Signature),
            ["AttestationKeyCertificate"] = Convert.ToHexString(quote.AttestationKeyCertificate)
        };
        var items = new List<JsonNode>
        {
            new JsonObject
            {
                ["Type"] = ReportType, ["Value"] = Base64Url(PtfCanonical.Bytes(report)), ["Verifier"] = partyId,
                ["HashAlgorithm"] = PtfIdentity.HashAlgorithm, ["HashValue"] = Convert.ToHexString(SHA256.HashData(quote.Attest))
            }
        };
        items.AddRange(log.Select(m => (JsonNode)new JsonObject
        {
            ["Type"] = MeasurementType, ["Value"] = Base64Url(Encoding.UTF8.GetBytes($"{m.Register} {m.What}")), ["Verifier"] = partyId,
            ["HashAlgorithm"] = PtfIdentity.HashAlgorithm, ["HashValue"] = m.Sha256
        }));
        var evidence = new JsonObject
        {
            ["Header"] = PtfEvidence.Header,
            ["AttestationEvidenceID"] = $"{partyId}#ATE-{Guid.NewGuid():N}",
            ["EvidenceItems"] = new JsonArray(items.ToArray())
        };
        return PtfSignature.Sign(evidence, identity, partyId);
    }

    // What a verifier approves: the manufacturer whose roots of trust it accepts, and
    // a judgement of each measurement - null where approved, otherwise why not.
    public sealed record Policy(X509Certificate2 Manufacturer, Func<Measurement, string?> Approve);

    // THE CHECK: null where the Evidence proves what it says; otherwise why not.
    public static string? Check(JsonObject evidence, ECDsa sender, string senderId, byte[] nonce, Policy policy, DateTimeOffset now)
    {
        if ((string?)evidence["Header"] != PtfEvidence.Header) return "its evidence is not Attestation Evidence";
        if ((string?)evidence["KeyID"] != senderId || PtfSignature.Verify(evidence, id => id == senderId ? sender : null) != PtfSignature.Outcome.Valid)
            return "its evidence is not signed by its identity key";

        var items = evidence["EvidenceItems"] as JsonArray ?? [];
        var reportItem = items.FirstOrDefault(i => (string?)i?["Type"] == ReportType);
        if (reportItem is null) return "its evidence has no quote";
        JsonObject report;
        byte[] attest, signature, certificate;
        try
        {
            report = JsonNode.Parse(FromBase64Url((string)reportItem["Value"]!))!.AsObject();
            attest = Convert.FromHexString((string)report["Quote"]!);
            signature = Convert.FromHexString((string)report["Signature"]!);
            certificate = Convert.FromHexString((string)report["AttestationKeyCertificate"]!);
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or NullReferenceException or InvalidOperationException)
        {
            return "its quote cannot be read";
        }

        // The attestation key: certified by the manufacturer this verifier trusts.
        using var ak = X509CertificateLoader.LoadCertificate(certificate);
        if (!ChainsTo(ak, policy.Manufacturer, now)) return "its attestation key is not certified by a manufacturer this verifier trusts";
        using var akKey = ak.GetECDsaPublicKey();
        if (akKey is null || !akKey.VerifyData(attest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            return "its quote is not signed by its attestation key";

        // The quote: of registers, over this verifier's nonce.
        if (Parse(attest) is not { } quoted) return "its quote is not a TPM 2.0 quote";
        if (!quoted.ExtraData.AsSpan().SequenceEqual(nonce)) return "replayed: its quote is not over the nonce this verifier chose";
        if (!quoted.Registers.SequenceEqual(Quoted)) return $"its quote is of registers {string.Join(",", quoted.Registers)}, not {string.Join(",", Quoted)}";

        // The log: replayed, it gives the registers quoted; each measurement approved.
        var log = new List<Measurement>();
        foreach (var item in items.Where(i => (string?)i?["Type"] == MeasurementType))
        {
            var text = Encoding.UTF8.GetString(FromBase64Url((string?)item!["Value"] ?? ""));
            var space = text.IndexOf(' ');
            if (space < 0 || !int.TryParse(text[..space], out var register)) return "a measurement of its log cannot be read";
            log.Add(new Measurement(register, text[(space + 1)..], (string?)item["HashValue"] ?? ""));
        }
        if (!Replay(log).AsSpan().SequenceEqual(quoted.PcrDigest)) return "its log is not what its registers hold";
        var problems = log.Select(m => policy.Approve(m)).OfType<string>().ToList();
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    // The digest of the quoted registers after the log: each register from zero,
    // extended in order, SHA-256 of their concatenation.
    public static byte[] Replay(IEnumerable<Measurement> log)
    {
        var registers = Quoted.ToDictionary(r => r, _ => new byte[32]);
        foreach (var m in log)
        {
            if (!registers.TryGetValue(m.Register, out var value)) continue;
            registers[m.Register] = SHA256.HashData([.. value, .. Convert.FromHexString(m.Sha256)]);
        }
        return SHA256.HashData(Quoted.SelectMany(r => registers[r]).ToArray());
    }

    private sealed record Quoted_(byte[] ExtraData, int[] Registers, byte[] PcrDigest);

    // TPMS_ATTEST of a quote (TPM 2.0 Part 2, 10.12.12): magic, type, qualifiedSigner,
    // extraData, clockInfo, firmwareVersion, then TPMS_QUOTE_INFO - the PCR selection
    // and the digest of the PCRs selected.
    private static Quoted_? Parse(byte[] a)
    {
        try
        {
            var at = 0;
            uint U32() { var v = BinaryPrimitives.ReadUInt32BigEndian(a.AsSpan(at)); at += 4; return v; }
            ushort U16() { var v = BinaryPrimitives.ReadUInt16BigEndian(a.AsSpan(at)); at += 2; return v; }
            byte[] Sized() { var n = U16(); var v = a.AsSpan(at, n).ToArray(); at += n; return v; }
            if (U32() != 0xFF544347 || U16() != 0x8018) return null;          // TPM_GENERATED_VALUE, TPM_ST_ATTEST_QUOTE
            Sized();                                                        // qualifiedSigner
            var extra = Sized();
            at += 17 + 8;                                                   // clockInfo, firmwareVersion
            var registers = new List<int>();
            var count = U32();
            for (var i = 0; i < count; i++)
            {
                var hash = U16();
                var size = a[at++];
                for (var b = 0; b < size; b++, at++)
                    for (var bit = 0; bit < 8; bit++)
                        if ((a[at] & (1 << bit)) != 0 && hash == 0x000B) registers.Add(b * 8 + bit);   // TPM_ALG_SHA256
            }
            return new Quoted_(extra, registers.ToArray(), Sized());
        }
        catch (ArgumentOutOfRangeException) { return null; }
        catch (IndexOutOfRangeException) { return null; }
    }

    private static bool ChainsTo(X509Certificate2 certificate, X509Certificate2 root, DateTimeOffset now)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        return chain.Build(certificate);
    }

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }
}

// THE SIMULATED MANUFACTURER of roots of trust (M3223 3.5): a certification authority
// that certifies, at manufacture, the endorsement key of each TPM it makes and the
// attestation key the TPM proves it holds beside it. A verifier trusts its root
// certificate, as it would a real manufacturer's.
public sealed class SimulatedManufacturer
{
    public X509Certificate2 Root { get; }
    private readonly ECDsa key;

    private SimulatedManufacturer(X509Certificate2 root, ECDsa key) { Root = root; this.key = key; }

    public static SimulatedManufacturer Make(string name = "MPAI simulated TPM manufacturer")
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        return new SimulatedManufacturer(X509CertificateLoader.LoadCertificate(root.RawData), key);
    }

    // The attestation key of the TPM whose endorsement key has this hash, certified.
    public byte[] CertifyAttestationKey(ECDsa attestationKey, string tpm, byte[] endorsementKeyHash)
    {
        var request = new CertificateRequest($"CN=Attestation key of {tpm}, OU=EK {Convert.ToHexString(endorsementKeyHash)[..16]}",
                                             attestationKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var issued = request.Create(Root.SubjectName, X509SignatureGenerator.CreateForECDsa(key),
                                          DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5), serial);
        return issued.RawData;
    }
}
