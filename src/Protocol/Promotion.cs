using System.Text.Json;
using System.Text.Json.Nodes;

namespace Diffra.Protocol;

/// <summary>Immutable record of verified two-run promotion and policy approval.</summary>
public sealed record PromotionApprovalRecord(
    string Schema,
    string DecisionId,
    string Issuer,
    string HeadCommit,
    string EvidenceId,
    string PayloadDigest,
    string PolicyId,
    string PolicyVersion,
    string RulesDigest,
    string VerificationRunEvidenceId,
    DateTimeOffset Timestamp)
{
    public const string SchemaUri = "https://diffra.dev/schemas/promotion-approval-v1.schema.json";

    public JsonObject ToJsonObject() => new()
    {
        ["schema"] = SchemaUri,
        ["decisionId"] = DecisionId,
        ["issuer"] = Issuer,
        ["headCommit"] = HeadCommit,
        ["evidenceId"] = EvidenceId,
        ["payloadDigest"] = PayloadDigest,
        ["policyId"] = PolicyId,
        ["policyVersion"] = PolicyVersion,
        ["rulesDigest"] = RulesDigest,
        ["verificationRunEvidenceId"] = VerificationRunEvidenceId,
        ["timestamp"] = Timestamp.ToString("O"),
    };

    public static PromotionApprovalRecord FromJsonElement(JsonElement root)
    {
        return new PromotionApprovalRecord(
            Schema: root.GetProperty("schema").GetString()!,
            DecisionId: root.GetProperty("decisionId").GetString()!,
            Issuer: root.GetProperty("issuer").GetString()!,
            HeadCommit: root.GetProperty("headCommit").GetString()!,
            EvidenceId: root.GetProperty("evidenceId").GetString()!,
            PayloadDigest: root.GetProperty("payloadDigest").GetString()!,
            PolicyId: root.GetProperty("policyId").GetString()!,
            PolicyVersion: root.GetProperty("policyVersion").GetString()!,
            RulesDigest: root.GetProperty("rulesDigest").GetString()!,
            VerificationRunEvidenceId: root.GetProperty("verificationRunEvidenceId").GetString()!,
            Timestamp: root.GetProperty("timestamp").GetDateTimeOffset());
    }
}

/// <summary>Controls promotion workflows requiring independent two-run verification and immutable approval records.</summary>
public static class PromotionManager
{
    /// <summary>
    /// Requires two independent production runs at the same head commit to yield identical payload digests
    /// before emitting an immutable approval record.
    /// </summary>
    public static PromotionApprovalRecord VerifyAndApprove(
        ReadOnlySpan<byte> run1EvidenceBytes,
        ReadOnlySpan<byte> run2EvidenceBytes,
        string headCommit,
        string policyId,
        string policyVersion,
        string rulesDigest,
        string issuer = "trusted-maintainer")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headCommit);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(rulesDigest);

        EvidenceIdentity.Verify(run1EvidenceBytes);
        EvidenceIdentity.Verify(run2EvidenceBytes);

        using var doc1 = CanonicalJson.Parse(run1EvidenceBytes);
        using var doc2 = CanonicalJson.Parse(run2EvidenceBytes);

        var root1 = doc1.RootElement;
        var root2 = doc2.RootElement;

        var id1 = root1.GetProperty("id").GetString()!;
        var id2 = root2.GetProperty("id").GetString()!;
        var digest1 = root1.GetProperty("payloadDigest").GetString()!;
        var digest2 = root2.GetProperty("payloadDigest").GetString()!;

        var commit1 = root1.GetProperty("subject").GetProperty("commit").GetString()!;
        var commit2 = root2.GetProperty("subject").GetProperty("commit").GetString()!;

        if (!string.Equals(commit1, headCommit, StringComparison.Ordinal) ||
            !string.Equals(commit2, headCommit, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Evidence commit does not match target head commit '{headCommit}'.");
        }

        if (!string.Equals(id1, id2, StringComparison.Ordinal) ||
            !string.Equals(digest1, digest2, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Flaky observation detected: Independent production runs yielded divergent digests ({digest1} vs {digest2}). Promotion refused.");
        }

        var decisionId = $"promote:{headCommit[..12]}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        return new PromotionApprovalRecord(
            Schema: PromotionApprovalRecord.SchemaUri,
            DecisionId: decisionId,
            Issuer: issuer,
            HeadCommit: headCommit,
            EvidenceId: id1,
            PayloadDigest: digest1,
            PolicyId: policyId,
            PolicyVersion: policyVersion,
            RulesDigest: rulesDigest,
            VerificationRunEvidenceId: id2,
            Timestamp: DateTimeOffset.UtcNow);
    }

    /// <summary>Validates that an approval record remains valid against current evidence and policy identities.</summary>
    public static (bool IsValid, string? InvalidationReason) ValidateApproval(
        PromotionApprovalRecord approval,
        ReadOnlySpan<byte> currentEvidenceBytes,
        string currentPolicyId,
        string currentRulesDigest)
    {
        ArgumentNullException.ThrowIfNull(approval);

        using var doc = CanonicalJson.Parse(currentEvidenceBytes);
        var currentId = doc.RootElement.GetProperty("id").GetString()!;
        var currentDigest = doc.RootElement.GetProperty("payloadDigest").GetString()!;

        if (!string.Equals(approval.EvidenceId, currentId, StringComparison.Ordinal))
        {
            return (false, $"Stale approval: approved Evidence ID '{approval.EvidenceId}' does not match current '{currentId}'.");
        }

        if (!string.Equals(approval.PayloadDigest, currentDigest, StringComparison.Ordinal))
        {
            return (false, $"Stale approval: approved Payload Digest '{approval.PayloadDigest}' does not match current '{currentDigest}'.");
        }

        if (!string.Equals(approval.PolicyId, currentPolicyId, StringComparison.Ordinal) ||
            !string.Equals(approval.RulesDigest, currentRulesDigest, StringComparison.Ordinal))
        {
            return (false, "Stale approval: underlying policy rules have changed since approval was granted.");
        }

        return (true, null);
    }
}
