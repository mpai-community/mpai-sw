using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AIF.Trust;

// A TRUST ANCHOR (MPAI-PTF V1.0, PTF-TRA): a key whose public part a verifier
// trusts, and which issues credentials. Its PTF object is what a verifier is given
// to trust it.
public sealed class TrustAnchorKey
{
    public const string Header = "PTF-TRA-V1.0";

    public string AnchorId { get; }
    public PtfIssuer Issuer { get; }
    public ECDsa PublicKey { get; }
    private readonly DateTimeOffset notBefore, notAfter;

    public TrustAnchorKey(string anchorId, ECDsa key, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        AnchorId = anchorId;
        Issuer = new PtfIssuer(anchorId, key, anchorId);
        PublicKey = ECDsa.Create(key.ExportParameters(false));
        this.notBefore = notBefore;
        this.notAfter = notAfter;
    }

    // The anchor as PTF has it. Its validity is a pair of Simple Times (OSD-STM), as
    // the author decided: each an absolute instant in milliseconds.
    public JsonObject Object() => new()
    {
        ["Header"] = Header,
        ["AnchorID"] = AnchorId,
        ["PublicKey"] = new JsonObject
        {
            ["Algorithm"] = PtfIdentity.SignatureAlgorithm, ["KeyEncoding"] = "spki",
            ["KeyValue"] = Convert.ToHexString(PublicKey.ExportSubjectPublicKeyInfo())
        },
        ["Validity"] = new JsonObject
        {
            ["NotBefore"] = TimeObject(AnchorId + "#NotBefore", notBefore),
            ["NotAfter"] = TimeObject(AnchorId + "#NotAfter", notAfter)
        }
    };

    // The public key a PTF Trust Anchor object names.
    public static ECDsa? KeyOf(JsonObject anchor)
    {
        if ((string?)anchor["Header"] != Header || (string?)anchor["PublicKey"]?["KeyEncoding"] != "spki") return null;
        try
        {
            var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromHexString((string?)anchor["PublicKey"]?["KeyValue"] ?? ""), out _);
            return key;
        }
        catch (Exception e) when (e is FormatException or CryptographicException) { return null; }
    }

    // The validity of a PTF Trust Anchor object: the instants its Simple Times state.
    public static (DateTimeOffset NotBefore, DateTimeOffset NotAfter)? ValidityOf(JsonObject anchor)
    {
        static DateTimeOffset? At(JsonNode? time) =>
            time?["SimpleTimeData"]?[0]?["StartTime"] is JsonValue ms && ms.TryGetValue<long>(out var v) ? DateTimeOffset.FromUnixTimeMilliseconds(v) : null;
        return At(anchor["Validity"]?["NotBefore"]) is { } from && At(anchor["Validity"]?["NotAfter"]) is { } to ? (from, to) : null;
    }

    // AN ANCHOR'S IDENTITY KEPT BETWEEN RUNS - a Controller's, a host's: its Trust
    // Anchor object, which the other parties are given, and its private key. Until a
    // root of trust holds the key (M3223 3.5, Step 6), a file holds it.
    public static void Save(string path, string anchorId, ECDsa key, DateTimeOffset notBefore, DateTimeOffset notAfter) =>
        File.WriteAllText(path, new JsonObject
        {
            ["Anchor"] = new TrustAnchorKey(anchorId, key, notBefore, notAfter).Object(),
            ["PrivateKey"] = Convert.ToHexString(key.ExportPkcs8PrivateKey())
        }.ToJsonString());

    public static (TrustAnchorKey Anchor, ECDsa Key) Load(string path)
    {
        var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var anchor = saved["Anchor"]!.AsObject();
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromHexString((string)saved["PrivateKey"]!), out _);
        var (from, to) = ValidityOf(anchor) ?? throw new InvalidDataException($"{path}: the anchor states no validity.");
        return (new TrustAnchorKey((string)anchor["AnchorID"]!, key, from, to), key);
    }

    // A Simple Time (OSD-STM-V1.5) of one instant: a segment whose start and end are
    // the instant, absolute (FlagsByte bit 0), in milliseconds (bits 1-2 = 01).
    private static JsonObject TimeObject(string id, DateTimeOffset t)
    {
        var ms = t.ToUnixTimeMilliseconds();
        return new()
        {
            ["Header"] = "OSD-STM-V1.5",
            ["SimpleTimeID"] = id,
            ["SimpleTimeData"] = new JsonArray(new JsonObject
            {
                ["FlagsByte"] = 3, ["StartTime"] = ms, ["EndTime"] = ms,
                ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 0
            })
        };
    }
}

// THE TRUST OF ONE CONTROLLER (Phase 13, M3223 3.1). In the AIF only AIM
// Instances are PTF Process Instances, and a Controller is itself a Trust Anchor
// (the author, 2026/09/25): its key is the anchor's, its Trust Anchor object is what
// hosts and other Controllers are given to trust it, and it issues the credentials
// of the AIM Instances it runs directly - every AIM Instance with a key of its own,
// its CII, the Instance Credential that binds that CII to the instance, and a
// Process Lifecycle Credential that follows the instance's state.
//
// The key of an AIM the Controller runs is made here, and never leaves this
// process. The key of an AIM on a host is made on the host, which sends only the
// CII; the Controller checks that the host holds the key before it issues the
// credential (IssueHeld).
public sealed class TrustDomain
{
    public static readonly TimeSpan CredentialLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan LifecycleLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan AnchorLifetime = TimeSpan.FromDays(365);

    public sealed class Instance
    {
        public required string Id { get; init; }
        public required JsonObject Cii { get; init; }
        public required JsonObject Credential { get; init; }
        public JsonObject Lifecycle { get; set; } = new();
        public List<string> States { get; } = new();
        public bool KeyHeldHere { get; init; }

        // The Attestation Evidence of what the instance runs (M3223 3.2).
        public JsonObject? Evidence { get; set; }

        // The Policy Binding the Controller bound it by, and the verdict of the
        // Verification Pipeline (M3223 3.3).
        public JsonObject? Policy { get; set; }
        public string Verdict { get; set; } = "not verified";

        // Where an AIM inside a package: the package, which issued its credential.
        public string? PackagedIn { get; init; }
    }

    public string ControllerId { get; }
    public TrustAnchorKey Anchor { get; }

    private readonly Func<DateTimeOffset> now;
    private readonly ECDsa anchorKey;
    private readonly ConcurrentDictionary<string, Instance> instances = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ECDsa> keys = new(StringComparer.Ordinal);

    public TrustDomain(string controllerId, Func<DateTimeOffset>? now = null)
    {
        ControllerId = controllerId;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        var t = this.now();
        anchorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Anchor = new TrustAnchorKey(controllerId, anchorKey, t, t + AnchorLifetime);
        Trace = new TrustTrace(Anchor, anchorKey, this.now);
    }

    // A Controller whose anchor is kept between runs: the hosts it uses are given
    // its Trust Anchor object. traceFile: where its Trace is kept (M3223 3.6), and
    // continued from, if it is there.
    public TrustDomain(TrustAnchorKey anchor, ECDsa key, Func<DateTimeOffset>? now = null, string? traceFile = null)
    {
        ControllerId = anchor.AnchorId;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        anchorKey = key;
        Anchor = anchor;
        Trace = new TrustTrace(Anchor, anchorKey, this.now, traceFile);
    }

    // THE TRACE of this Controller's trust decisions, a chain (M3223 3.6).
    public TrustTrace Trace { get; }

    // THE TRUST PROTOCOL of this Controller with the hosts whose anchors it trusts
    // (M3223 3.4).
    public TrustProtocol LinkWith(IEnumerable<JsonObject> hostAnchors, IRootOfTrust? attestor = null, Attestation.Policy? requires = null) =>
        new(Anchor, anchorKey, hostAnchors, now, attestor, requires, (type, targetType, targetId, failure) => Operation(type, targetType, targetId, failure));

    // The public key a KeyID names, among those this Controller trusts: its own.
    public ECDsa? KeyFor(string keyId) => keyId == Anchor.AnchorId ? Anchor.PublicKey : null;

    // AN AIM THE CONTROLLER RUNS: its key made here. An issuer - a package that
    // credentials the AIMs inside it (M3223 3.3) - is entitled to issue by its scope.
    public Instance IssueLocal(string instanceId, string aimInstance, string? implementation, bool issuer = false)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        keys[instanceId] = key;
        return Issue(instanceId, aimInstance, PtfIdentity.Make(key, instanceId, implementation, now()), heldHere: true, issuer);
    }

    // AN AIM ON A HOST: its key made there, its CII sent here. Refused unless the CII
    // shows the host holds the key it names.
    public Instance IssueHeld(string instanceId, string aimInstance, JsonObject cii)
    {
        if (PtfIdentity.Check(cii, out _) is var checkedCii && checkedCii != PtfIdentity.Outcome.Valid)
            throw new InvalidOperationException($"{instanceId}: its CII is not valid ({checkedCii}); no credential issued.");
        if ((string?)cii["CryptographicInstanceID"] != instanceId)
            throw new InvalidOperationException($"{instanceId}: its CII names {cii["CryptographicInstanceID"]}; no credential issued.");
        return Issue(instanceId, aimInstance, cii, heldHere: false, issuer: false);
    }

    private Instance Issue(string instanceId, string aimInstance, JsonObject cii, bool heldHere, bool issuer)
    {
        var instance = new Instance
        {
            Id = instanceId,
            Cii = cii,
            Credential = Anchor.Issuer.IssueCredential(cii, "AIMInstance", instanceId, PtfIdentity.AimType(aimInstance), now(),
                                                        CredentialLifetime, issuer ? PtfCredential.IssuerScope : null),
            KeyHeldHere = heldHere
        };
        instances[instanceId] = instance;
        Operation("IssueCredential", "InstanceCredential", (string?)instance.Credential["InstanceCredentialID"] ?? instanceId, null);
        Transition(instance, "Created");
        return instance;
    }

    // THE LIFECYCLE: a new Process Lifecycle Credential at each change of state.
    public void Transition(string instanceId, string state)
    {
        if (instances.TryGetValue(instanceId, out var instance)) Transition(instance, state);
    }

    // Every instance of a Module instance (<Module instance>/<AIM>).
    public void TransitionModule(string moduleInstance, string state)
    {
        foreach (var instance in instances.Values.Where(i => i.Id.StartsWith(moduleInstance + "/", StringComparison.Ordinal)))
            Transition(instance, state);
    }

    private void Transition(Instance instance, string state)
    {
        lock (instance)
        {
            instance.Lifecycle = Anchor.Issuer.IssueLifecycle(instance.Id, state, now(), LifecycleLifetime);
            instance.States.Add(state);
            Operation("UpdateLifecycleState", "ProcessLifecycleCredential", (string?)instance.Lifecycle["ProcessLifecycleCredentialID"] ?? instance.Id, null);
        }
    }

    public Instance? Of(string instanceId) => instances.GetValueOrDefault(instanceId);

    // ---- the Verifier (M3223 3.3) -------------------------------------------------

    // Every trust decision and act of this Controller, as PTF Trust Operations
    // (PTF-TOP), in the order made - for the Trace (M3223 3.6).
    public IReadOnlyList<JsonObject> Operations => Trace.Records.Select(r => r["TrustOperation"]!.AsObject()).ToList();

    public JsonObject Operation(string type, string targetType, string targetId, string? failure)
    {
        var operation = new JsonObject
        {
            ["Header"] = "PTF-TOP-V1.0",
            ["TrustOperationID"] = $"{ControllerId}#TOP-{Guid.NewGuid():N}",
            ["TrustOperationTime"] = new JsonObject
            {
                ["Header"] = "OSD-TIM-V1.5", ["TimeID"] = $"{ControllerId}#TIM-{Guid.NewGuid():N}", ["Data"] = PtfIdentity.Time(now())
            },
            ["OperationType"] = type,
            ["TargetType"] = targetType,
            ["TargetID"] = targetId,
            ["ActorID"] = ControllerId,
            ["Status"] = failure is null ? "Success" : "Failure"
        };
        if (failure is not null) operation["FailureReason"] = failure;
        PtfSignature.Sign(operation, anchorKey, Anchor.AnchorId);
        Trace.Append(operation);
        return operation;
    }

    // THE POLICY an AIM Instance is bound by (PTF-POL): what the approved Metadata
    // allows it - the Ports it may read and write, the storage it is given - bound
    // by this Controller, the Policy Authority of its AIMs.
    public JsonObject BindPolicy(string instanceId, IEnumerable<(string Name, string Value)> constraints)
    {
        var policy = new JsonObject
        {
            ["Header"] = "PTF-POL-V1.0",
            ["PolicyID"] = $"{instanceId}#POL-{Guid.NewGuid():N}",
            ["Target"] = new JsonObject { ["Type"] = "AIMInstance", ["ID"] = instanceId },
            ["Constraints"] = new JsonArray(constraints.Select(c => (JsonNode)new JsonObject { ["Name"] = c.Name, ["Value"] = c.Value }).ToArray())
        };
        PtfSignature.Sign(policy, anchorKey, Anchor.AnchorId);
        if (instances.TryGetValue(instanceId, out var instance)) instance.Policy = policy;
        Operation("BindPolicy", "PolicyBinding", (string)policy["PolicyID"]!, null);
        return policy;
    }

    // AN AIM INSIDE A PACKAGE, as the package presented it: its CII and the
    // credential the package issued it (M3223 3.3).
    public Instance RecordPackaged(string instanceId, string packageId, JsonObject cii, JsonObject credential, JsonObject? evidence)
    {
        var instance = new Instance { Id = instanceId, Cii = cii, Credential = credential, KeyHeldHere = false, PackagedIn = packageId, Evidence = evidence };
        instances[instanceId] = instance;
        return instance;
    }

    // Runs the Verification Pipeline on an instance, as it presents itself, and
    // records the verdict.
    public VerificationPipeline.Decision Verify(Instance instance, VerificationPipeline.Expectation expectation,
                                                IReadOnlyList<PtfCredential.Link>? chain = null)
    {
        var decision = VerificationPipeline.Run(
            new VerificationPipeline.Presentation(instance.Cii, instance.Credential,
                instance.Lifecycle.Count > 0 ? instance.Lifecycle : null, instance.Evidence, chain),
            expectation, KeyFor, now(), Operation);
        instance.Verdict = decision.Trusted ? "trusted" : "not trusted: " + decision.Reason;
        return decision;
    }

    // The evidence of what an AIM the Controller runs will run, measured by the
    // Controller and signed by it.
    public JsonObject SignEvidence(string instanceId, IEnumerable<PtfEvidence.Item> items) =>
        PtfEvidence.Make(instanceId, items, ControllerId, anchorKey, Anchor.AnchorId);

    public IReadOnlyList<Instance> OfModule(string moduleInstance) =>
        instances.Values.Where(i => i.Id.StartsWith(moduleInstance + "/", StringComparison.Ordinal)).OrderBy(i => i.Id, StringComparer.Ordinal).ToList();

    // The signing key of an AIM the Controller runs: for that AIM, and nothing else.
    public ECDsa? KeyOf(string instanceId) => keys.GetValueOrDefault(instanceId);
}
