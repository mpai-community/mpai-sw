using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using AIF.Trust;
using Tpm2Lib;

namespace AIF.RootOfTrust;

// A PARTY'S ROOT OF TRUST: a TPM 2.0 - Microsoft's reference simulator, reached
// over TCP - as a Controller or an AIM host has one (M3223 3.5). Connecting boots
// it: its registers start from zero, as at power-on. It holds:
//   - an endorsement key (EK), certified with the attestation key by its
//     manufacturer at enrolment;
//   - an attestation key (AK), restricted: it signs only what the TPM itself
//     states - its quotes;
//   - the party's identity key, which signs the party's messages and never
//     leaves it.
// All three are primary keys, derived from the TPM's seeds: the same each time the
// TPM is booted, never stored outside it. The interface has no function that
// returns a private key.
public sealed class SimulatedTpm : IRootOfTrust, IDisposable
{
    private readonly Tpm2Device device;
    private readonly Tpm2 tpm;
    private readonly object gate = new();
    private readonly TpmHandle identity;
    private TpmHandle? quoting;                                    // the AK, kept loaded between quotes
    private readonly TpmPublic ekPublic, akPublic;
    private readonly List<Attestation.Measurement> log = new();

    public string Name { get; }
    public ECDsa IdentityKey { get; }

    // The certificate of the attestation key, once enrolled.
    public byte[]? AttestationKeyCertificate { get; private set; }

    public SimulatedTpm(string name, string host, int port, string identityLabel)
    {
        Name = name;
        device = new TcpTpmDevice(host, port, false, false);
        device.Connect();
        tpm = new Tpm2(device);
        device.PowerCycle();                                   // a boot: registers from zero
        tpm.Startup(Su.Clear);

        // Only the identity key stays loaded: a TPM holds few objects at once. The
        // others are made again - the same keys - when they are needed.
        ekPublic = With(TpmRh.Endorsement, EndorsementTemplate(), (_, p) => p);
        akPublic = With(TpmRh.Endorsement, AkTemplate, (_, p) => p);
        identity = Primary(TpmRh.Owner, EccTemplate(ObjectAttr.Sign, "identity " + identityLabel), out var identityPublic);
        IdentityKey = new TpmECDsa(this, identity, PublicOf(identityPublic));
    }

    // ---- keys ----------------------------------------------------------------------

    private TpmHandle Primary(TpmRh hierarchy, TpmPublic template, out TpmPublic made)
    {
        lock (gate)
            return tpm[new AuthValue()].CreatePrimary(hierarchy, new SensitiveCreate(), template, [], [],
                                                      out made, out _, out _, out _);
    }

    // A primary key made for one use, then flushed. The AK kept for quoting is flushed
    // first: the TPM holds three objects at most.
    private T With<T>(TpmRh hierarchy, TpmPublic template, Func<TpmHandle, TpmPublic, T> use)
    {
        lock (gate)
        {
            if (quoting is { } kept) { tpm.FlushContext(kept); quoting = null; }
            var handle = Primary(hierarchy, template, out var made);
            try { return use(handle, made); }
            finally { tpm.FlushContext(handle); }
        }
    }

    private static TpmPublic AkTemplate => EccTemplate(ObjectAttr.Sign | ObjectAttr.Restricted, "AK");

    // Keys of the TPM, used without an authorisation value: exempt from its
    // dictionary-attack lockout, which counts the unorderly shutdowns of a simulator.
    private const ObjectAttr Held = ObjectAttr.FixedTPM | ObjectAttr.FixedParent | ObjectAttr.SensitiveDataOrigin | ObjectAttr.UserWithAuth | ObjectAttr.NoDA;

    private static TpmPublic EccTemplate(ObjectAttr use, string label) =>
        new(TpmAlgId.Sha256, Held | use, [],
            new EccParms(new SymDefObject(TpmAlgId.Null, 0, TpmAlgId.Null), new SchemeEcdsa(TpmAlgId.Sha256), EccCurve.TpmEccNistP256, new NullKdfScheme()),
            new EccPoint(SHA256.HashData(Encoding.UTF8.GetBytes(label)), []));

    private static TpmPublic EndorsementTemplate() =>
        new(TpmAlgId.Sha256, Held | ObjectAttr.Restricted | ObjectAttr.Decrypt, [],
            new RsaParms(new SymDefObject(TpmAlgId.Aes, 128, TpmAlgId.Cfb), new NullAsymScheme(), 2048, 0),
            new Tpm2bPublicKeyRsa(new byte[256]));

    private static TpmPublic StorageTemplate() =>
        new(TpmAlgId.Sha256, Held | ObjectAttr.Restricted | ObjectAttr.Decrypt, [],
            new EccParms(new SymDefObject(TpmAlgId.Aes, 128, TpmAlgId.Cfb), new NullAsymScheme(), EccCurve.TpmEccNistP256, new NullKdfScheme()),
            new EccPoint(SHA256.HashData("storage"u8.ToArray()), []));

    private static ECDsa PublicOf(TpmPublic key)
    {
        var point = (EccPoint)key.unique;
        return ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = Pad(point.x), Y = Pad(point.y) } });
    }

    private static byte[] Pad(byte[] v) => v.Length >= 32 ? v : [.. new byte[32 - v.Length], .. v];

    // Signs a digest with a key held here: r||s.
    internal byte[] Sign(TpmHandle key, byte[] digest)
    {
        lock (gate)
        {
            var signature = (SignatureEcdsa)tpm.Sign(key, digest, new SchemeEcdsa(TpmAlgId.Sha256), new TkHashcheck(TpmRh.Null, []));
            return [.. Pad(signature.signatureR), .. Pad(signature.signatureS)];
        }
    }

    // A key held here cannot be taken out of it: asked to duplicate the identity key,
    // the TPM refuses. The response code, for the tests.
    public TpmRc TryDuplicateIdentityKey()
    {
        lock (gate)
        {
            tpm[new AuthValue()]._AllowErrors().Duplicate(identity, TpmRh.Null, [], new SymDefObject(TpmAlgId.Null, 0, TpmAlgId.Null), out _, out _);
            return tpm._GetLastResponseCode();
        }
    }

    // ---- enrolment ------------------------------------------------------------------

    // THE MANUFACTURER ENROLS THE TPM: it makes a credential that only the TPM holding
    // its endorsement key can open, bound to the name of the attestation key; the TPM
    // opens it - proving the attestation key is its own - and the manufacturer
    // certifies the attestation key.
    public void Enroll(SimulatedManufacturer manufacturer)
    {
        var secret = RandomNumberGenerator.GetBytes(16);
        var blob = ekPublic.CreateActivationCredentials(secret, akPublic.GetName(), out var encrypted);
        var opened = With(TpmRh.Endorsement, EndorsementTemplate(), (ek, _) =>
            With(TpmRh.Endorsement, AkTemplate, (ak, _) => tpm[new AuthValue(), new AuthValue()].ActivateCredential(ak, ek, blob, encrypted)));
        if (!opened.AsSpan().SequenceEqual(secret)) throw new CryptographicException($"{Name}: its attestation key is not held beside its endorsement key.");
        using var akKey = PublicOf(akPublic);
        AttestationKeyCertificate = manufacturer.CertifyAttestationKey(akKey, Name, SHA256.HashData(ekPublic.GetTpmRepresentation()));
    }

    // The certificate of an enrolment made earlier: only if it certifies this TPM's
    // attestation key.
    public void UseCertificate(byte[] certificate)
    {
        using var certified = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(certificate);
        using var named = certified.GetECDsaPublicKey();
        using var akKey = PublicOf(akPublic);
        if (named is null || !named.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(akKey.ExportSubjectPublicKeyInfo()))
            throw new CryptographicException($"{Name}: the certificate is not of its attestation key.");
        AttestationKeyCertificate = certificate;
    }

    // ---- measurement, quote, seal ------------------------------------------------------

    public IReadOnlyList<Attestation.Measurement> Log { get { lock (gate) return log.ToList(); } }

    public void Measure(int register, string what, string sha256)
    {
        lock (gate)
        {
            tpm.PcrExtend(TpmHandle.Pcr(register), [new TpmHash(TpmAlgId.Sha256, Convert.FromHexString(sha256))]);
            log.Add(new Attestation.Measurement(register, what, sha256.ToUpperInvariant()));
        }
    }

    private static PcrSelection[] Registers => [new PcrSelection(TpmAlgId.Sha256, Attestation.Quoted.Select(r => (uint)r), 24)];

    public Attestation.Quote Quote(byte[] nonce)
    {
        var certificate = AttestationKeyCertificate ?? throw new InvalidOperationException($"{Name} is not enrolled: its attestation key is not certified.");
        lock (gate)
        {
            var ak = quoting ??= Primary(TpmRh.Endorsement, AkTemplate, out _);
            var attest = tpm.Quote(ak, nonce, new SchemeEcdsa(TpmAlgId.Sha256), Registers, out var signature);
            var ecdsa = (SignatureEcdsa)signature;
            return new Attestation.Quote(attest.GetTpmRepresentation(), [.. Pad(ecdsa.signatureR), .. Pad(ecdsa.signatureS)], certificate);
        }
    }

    public JsonObject Evidence(string partyId, byte[] nonce)
    {
        var quote = Quote(nonce);
        return Attestation.Evidence(partyId, quote, Log, IdentityKey);
    }

    // SEALS a secret to the registers as they are now: the TPM releases it only while
    // they hold these values.
    public byte[] Seal(byte[] secret)
    {
        return With(TpmRh.Owner, StorageTemplate(), (storage, sealedIn) =>
        {
            var policy = PcrPolicy(out _);
            var template = new TpmPublic(TpmAlgId.Sha256, ObjectAttr.FixedTPM | ObjectAttr.FixedParent, policy.GetPolicyDigest(),
                                         new KeyedhashParms(new NullSchemeKeyedhash()), new Tpm2bDigestKeyedhash());
            var sealedPrivate = tpm[new AuthValue()].Create(storage, new SensitiveCreate([], secret), template, [], [], out var sealedPublic, out _, out _, out _);
            return (byte[])[.. BitConverter.GetBytes(sealedPublic.GetTpmRepresentation().Length), .. sealedPublic.GetTpmRepresentation(), .. sealedPrivate.GetTpmRepresentation()];
        });
    }

    // The secret, or null where the registers no longer hold what it was sealed to.
    public byte[]? Unseal(byte[] sealedBlob)
    {
        return With(TpmRh.Owner, StorageTemplate(), (storage, sealedIn) =>
        {
            var publicLength = BitConverter.ToInt32(sealedBlob, 0);
            var sealedPublic = Marshaller.FromTpmRepresentation<TpmPublic>(sealedBlob.AsSpan(4, publicLength).ToArray());
            var sealedPrivate = Marshaller.FromTpmRepresentation<TpmPrivate>(sealedBlob.AsSpan(4 + publicLength).ToArray());
            var handle = tpm[new AuthValue()].Load(storage, sealedPrivate, sealedPublic);
            var session = tpm.StartAuthSessionEx(TpmSe.Policy, TpmAlgId.Sha256);
            try
            {
                tpm.PolicyPCR(session.Handle, [], Registers);
                var opened = tpm[session]._AllowErrors().Unseal(handle);
                return tpm._LastCommandSucceeded() ? opened : null;
            }
            finally
            {
                tpm.FlushContext(session);
                tpm.FlushContext(handle);
            }
        });
    }

    // The policy of the registers as they are now.
    private PolicyTree PcrPolicy(out Tpm2bDigest[] values)
    {
        tpm.PcrRead(Registers, out var selected, out values);
        var tree = new PolicyTree(TpmAlgId.Sha256);
        tree.Create([new TpmPolicyPcr(new PcrValueCollection(selected, values))]);
        return tree;
    }

    // An orderly shutdown, as at power-off.
    public void Dispose()
    {
        IdentityKey.Dispose();
        lock (gate)
        {
            try { tpm._AllowErrors().Shutdown(Su.Clear); } catch (Exception) { }
            tpm.Dispose();
        }
    }
}

// THE PARTY'S IDENTITY KEY, AS .NET SEES IT: an ECDsa whose signatures the TPM makes.
// Its private key cannot be exported: there is none here.
internal sealed class TpmECDsa : ECDsa
{
    private readonly SimulatedTpm tpm;
    private readonly TpmHandle handle;
    private readonly ECDsa publicKey;

    public TpmECDsa(SimulatedTpm tpm, TpmHandle handle, ECDsa publicKey)
    {
        this.tpm = tpm;
        this.handle = handle;
        this.publicKey = publicKey;
        KeySizeValue = 256;
        LegalKeySizesValue = [new KeySizes(256, 256, 0)];
    }

    public override byte[] SignHash(byte[] hash) => tpm.Sign(handle, hash);
    public override bool VerifyHash(byte[] hash, byte[] signature) => publicKey.VerifyHash(hash, signature);

    protected override byte[] HashData(byte[] data, int offset, int count, HashAlgorithmName hashAlgorithm) =>
        hashAlgorithm == HashAlgorithmName.SHA256 ? SHA256.HashData(data.AsSpan(offset, count)) : throw new CryptographicException("SHA-256 only.");

    public override ECParameters ExportParameters(bool includePrivateParameters) =>
        includePrivateParameters
            ? throw new CryptographicException("The private key is held by the TPM, which does not release it.")
            : publicKey.ExportParameters(false);

    public override ECParameters ExportExplicitParameters(bool includePrivateParameters) => ExportParameters(includePrivateParameters);
    public override void ImportParameters(ECParameters parameters) => throw new CryptographicException("A key held by the TPM is not imported.");
    public override byte[] ExportPkcs8PrivateKey() => throw new CryptographicException("The private key is held by the TPM, which does not release it.");
    public override byte[] ExportECPrivateKey() => throw new CryptographicException("The private key is held by the TPM, which does not release it.");

    protected override void Dispose(bool disposing)
    {
        if (disposing) publicKey.Dispose();
        base.Dispose(disposing);
    }
}
