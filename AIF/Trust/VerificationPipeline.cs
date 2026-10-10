using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AIF.Trust;

// THE VERIFICATION PIPELINE (MPAI-PTF V1.0, Verification Pipeline and Trust
// Establishment Protocol), as the Controller runs it - PTF's Verifier (M3223 3.3) -
// for an AIM Instance before it runs: its identity, its credential through the
// chain presented with it, its lifecycle, the evidence of what it runs, and the
// policy it is bound by. Deterministic: the first check that fails ends it, with
// the reason. Each check is a Trust Operation (PTF-TOP), signed by the verifier, for
// the Trace.
public static class VerificationPipeline
{
    // What an instance presents - PTF's Presentation.
    public sealed record Presentation(
        JsonObject Cii, JsonObject Credential, JsonObject? Lifecycle = null, JsonObject? Evidence = null,
        IReadOnlyList<PtfCredential.Link>? Chain = null);

    // What the verifier expects of it: the instance it must be, the state it must be
    // in, what its evidence must show, and the policy it must be bound by.
    public sealed record Expectation(
        string InstanceId,
        string? LifecycleState = null,
        bool EvidenceRequired = false,
        Func<IReadOnlyList<PtfEvidence.Item>, IReadOnlyList<string>>? CheckEvidence = null,
        JsonObject? Policy = null,
        Func<JsonObject, IReadOnlyList<string>>? CheckPolicy = null);

    public sealed record Decision(bool Trusted, string Reason, IReadOnlyList<JsonObject> Operations);

    // operation: records one check as a Trust Operation - its type, the type and
    // identifier of what was checked, and its failure where it failed.
    public static Decision Run(Presentation p, Expectation e, Func<string, ECDsa?> anchors, DateTimeOffset now,
                               Func<string, string, string, string?, JsonObject> operation)
    {
        var operations = new List<JsonObject>();
        Decision Fail(string type, string targetType, string targetId, string reason)
        {
            operations.Add(operation(type, targetType, targetId, reason));
            return new Decision(false, reason, operations);
        }
        void Pass(string type, string targetType, string targetId) => operations.Add(operation(type, targetType, targetId, null));

        // 1. IDENTITY: the CII is well formed, signed by the key it names - its holder
        // holds it - and is the identity of the instance expected.
        var ciiId = (string?)p.Cii["CryptographicInstanceID"] ?? "";
        var identity = PtfIdentity.Check(p.Cii, out var instanceKey);
        if (identity != PtfIdentity.Outcome.Valid) return Fail("VerifySignature", "CII", ciiId, $"its CII: {identity}");
        if (ciiId != e.InstanceId) return Fail("VerifySignature", "CII", ciiId, $"its CII names {ciiId}, not {e.InstanceId}");
        Pass("VerifySignature", "CII", ciiId);

        // 2. CREDENTIAL: issued, through the chain presented, by an anchor the verifier
        // trusts; for this CII; current; for an AIM Instance - in the AIF the only
        // Process Instance - and for this one.
        var credentialId = (string?)p.Credential["InstanceCredentialID"] ?? "";
        var chained = PtfCredential.CheckChain(p.Credential, p.Cii, p.Chain ?? [], anchors, now);
        if (chained != PtfCredential.Outcome.Valid) return Fail("ValidateCredential", "InstanceCredential", credentialId, $"its credential: {chained}");
        if ((string?)p.Credential["Subject"]?["InstanceType"] != "AIMInstance" || (string?)p.Credential["Subject"]?["InstanceID"] != e.InstanceId)
            return Fail("ValidateCredential", "InstanceCredential", credentialId, "its credential is not for this AIM Instance");
        Pass("ValidateCredential", "InstanceCredential", credentialId);

        // 3. LIFECYCLE: in the state expected, stated by an issuer trusted.
        if (e.LifecycleState is { } state)
        {
            var lifecycleId = (string?)p.Lifecycle?["ProcessLifecycleCredentialID"] ?? "";
            if (p.Lifecycle is null) return Fail("ValidateCredential", "ProcessLifecycleCredential", lifecycleId, "no lifecycle credential");
            var lifecycle = PtfCredential.CheckLifecycle(p.Lifecycle, e.InstanceId, state, anchors, now);
            if (lifecycle != PtfCredential.Outcome.Valid) return Fail("ValidateCredential", "ProcessLifecycleCredential", lifecycleId, $"its lifecycle: {lifecycle}");
            Pass("ValidateCredential", "ProcessLifecycleCredential", lifecycleId);
        }

        // 4. EVIDENCE: signed by the party that measured - the verifier, or the
        // instance itself - and showing what the verifier expects.
        if (p.Evidence is { } evidence)
        {
            var evidenceId = (string?)evidence["AttestationEvidenceID"] ?? "";
            var signed = PtfSignature.Verify(evidence, id => anchors(id) ?? (id == ciiId ? instanceKey : null));
            if (signed != PtfSignature.Outcome.Valid) return Fail("ValidateEvidence", "AttestationEvidence", evidenceId, $"its evidence: {signed}");
            var problems = e.CheckEvidence?.Invoke(PtfEvidence.Items(evidence)) ?? [];
            if (problems.Count > 0) return Fail("ValidateEvidence", "AttestationEvidence", evidenceId, string.Join("; ", problems));
            Pass("ValidateEvidence", "AttestationEvidence", evidenceId);
        }
        else if (e.EvidenceRequired) return Fail("ValidateEvidence", "AttestationEvidence", "", "no evidence of what it runs");

        // 5. POLICY: the Policy Binding the verifier bound it by, intact, for it, and
        // what the approved Metadata allows it.
        if (e.Policy is { } policy)
        {
            var policyId = (string?)policy["PolicyID"] ?? "";
            if (PtfSignature.Verify(policy, anchors) != PtfSignature.Outcome.Valid) return Fail("EvaluatePolicy", "PolicyBinding", policyId, "its policy is not the verifier's");
            if ((string?)policy["Target"]?["ID"] != e.InstanceId) return Fail("EvaluatePolicy", "PolicyBinding", policyId, "its policy is for another instance");
            var problems = e.CheckPolicy?.Invoke(policy) ?? [];
            if (problems.Count > 0) return Fail("EvaluatePolicy", "PolicyBinding", policyId, string.Join("; ", problems));
            Pass("EvaluatePolicy", "PolicyBinding", policyId);
        }
        return new Decision(true, "trusted", operations);
    }
}
