using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Diffra.Protocol;

/// <summary>Exact policy version and thresholds evaluated against a Delta.</summary>
public sealed record NumericPolicy(
    string Id,
    string Version,
    string RulesDigest,
    decimal? WarnAboveAbsoluteIncrease = null,
    decimal? FailAboveAbsoluteIncrease = null,
    decimal? WarnAboveRelativeIncrease = null,
    decimal? FailAboveRelativeIncrease = null);

/// <summary>Policy identity included in an assessment.</summary>
public sealed record PolicyProvenance(string Id, string Version, string RulesDigest);

/// <summary>Finding emitted while evaluating a numeric delta.</summary>
public sealed record AssessmentFinding(
    string Id,
    string Status,
    string Message,
    IReadOnlyList<string> ChangePaths,
    object? Observed,
    object? Expected);

/// <summary>Exact evaluation implementation and Delta identity.</summary>
public sealed record AssessmentProvenance(string Evaluator, string Version, string InputDeltaId);

/// <summary>Policy result, kept separate from the factual Delta.</summary>
public sealed record AssessmentDocument(
    string Schema,
    string Id,
    string DeltaId,
    string DeltaStatus,
    PolicyProvenance Policy,
    string Outcome,
    IReadOnlyList<AssessmentFinding> Findings,
    AssessmentProvenance Provenance);

/// <summary>Evaluates thresholds over an existing Delta without rerunning comparison.</summary>
public static class AssessmentEvaluator
{
    public const string AssessmentSchema = "https://diffra.dev/schemas/assessment-v1.schema.json";
    public const string EvaluatorId = "diffra.numeric-policy";
    public const string EvaluatorVersion = "1.0.0";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static AssessmentDocument Evaluate(DeltaDocument delta, NumericPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(delta);
        ArgumentNullException.ThrowIfNull(policy);
        ValidatePolicy(policy);

        var policyProvenance = new PolicyProvenance(policy.Id, policy.Version, policy.RulesDigest);
        var provenance = new AssessmentProvenance(EvaluatorId, EvaluatorVersion, delta.Id);
        var findings = new List<AssessmentFinding>();
        string outcome;

        if (delta.Status is "unavailable" or "invalid" or "incompatible" or "inconclusive" or "unsupported")
        {
            outcome = "inconclusive";
            findings.Add(new AssessmentFinding(
                "comparison-not-assessable",
                "inconclusive",
                delta.Message ?? $"Delta status '{delta.Status}' cannot be assessed as a policy pass.",
                [],
                delta.Status,
                "changed or unchanged"));
        }
        else if (delta.Status == "unchanged")
        {
            outcome = "pass";
            findings.Add(new AssessmentFinding("numeric-policy", "pass", "No numeric changes were found.", [], 0, 0));
        }
        else if (delta.Status == "changed")
        {
            foreach (var change in delta.Changes)
            {
                if (change.Kind is "added" or "removed" or "missing" or "unavailable" or "incompatible")
                {
                    findings.Add(new AssessmentFinding(
                        $"numeric-policy:{change.Path}",
                        "needs-review",
                        $"Numeric observation at '{change.Path}' is {change.Kind} and requires review.",
                        [change.Path],
                        change.Kind,
                        "modified numeric value"));
                    continue;
                }

                if (change.Kind != "modified" ||
                    !TryParseDecimal(change.Value.Before, out var before) ||
                    !TryParseDecimal(change.Value.Absolute, out var absolute) ||
                    (change.Value.Relative is null && (before != 0 || change.Value.RelativeStatus != "undefined-zero-baseline")) ||
                    (change.Value.Relative is not null && !TryParseDecimal(change.Value.Relative, out _)))
                {
                    findings.Add(new AssessmentFinding(
                        $"numeric-policy:{change.Path}",
                        "inconclusive",
                        $"Numeric change at '{change.Path}' is missing comparable delta values.",
                        [change.Path],
                        change.Value,
                        "before, after, and delta"));
                    continue;
                }

                decimal? relative = change.Value.Relative is null ? null : decimal.Parse(change.Value.Relative, System.Globalization.CultureInfo.InvariantCulture);
                var severity = SeverityFor(absolute, relative, policy);
                var status = severity switch { 3 => "needs-review", 2 => "fail", 1 => "warn", _ => "pass" };
                var limit = severity switch
                {
                    3 => (object?)new { absolute = Format(policy.FailAboveAbsoluteIncrease ?? policy.WarnAboveAbsoluteIncrease), relative = Format(policy.FailAboveRelativeIncrease ?? policy.WarnAboveRelativeIncrease) },
                    2 => (object?)new { absolute = Format(policy.FailAboveAbsoluteIncrease), relative = Format(policy.FailAboveRelativeIncrease) },
                    1 => new { absolute = Format(policy.WarnAboveAbsoluteIncrease), relative = Format(policy.WarnAboveRelativeIncrease) },
                    _ => new { absolute = Format(policy.WarnAboveAbsoluteIncrease ?? policy.FailAboveAbsoluteIncrease), relative = Format(policy.WarnAboveRelativeIncrease ?? policy.FailAboveRelativeIncrease) }
                };
                findings.Add(new AssessmentFinding(
                    $"numeric-policy:{change.Path}",
                    status,
                    status == "pass"
                        ? $"Numeric increase at '{change.Path}' is within policy limits."
                        : status == "needs-review"
                            ? $"Relative increase at '{change.Path}' is undefined because its baseline is zero."
                        : $"Numeric increase at '{change.Path}' exceeds the {status} threshold.",
                    [change.Path],
                    change.Value,
                    limit));
            }

            outcome = findings.Any(f => f.Status == "fail") ? "fail"
                : findings.Any(f => f.Status == "inconclusive") ? "inconclusive"
                : findings.Any(f => f.Status == "needs-review") ? "needs-review"
                : findings.Any(f => f.Status == "warn") ? "warn"
                : "pass";
        }
        else
        {
            outcome = "inconclusive";
            findings.Add(new AssessmentFinding(
                "unknown-delta-status",
                "inconclusive",
                $"Delta status '{delta.Status}' is not recognized by this evaluator.",
                [],
                delta.Status,
                "changed or unchanged"));
        }

        return CreateAssessment(delta.Id, delta.Status, policyProvenance, outcome, findings, provenance);
    }

    private static int SeverityFor(decimal absolute, decimal? relative, NumericPolicy policy)
    {
        // Budgets describe increases. A decrease is not a regression for these rules.
        if (absolute <= 0) return 0;
        if (relative is null && (policy.WarnAboveRelativeIncrease is not null || policy.FailAboveRelativeIncrease is not null)) return 3;
        if (policy.FailAboveAbsoluteIncrease is { } absoluteFail && absolute > absoluteFail) return 2;
        if (relative is { } relativeFailValue && policy.FailAboveRelativeIncrease is { } relativeFail && relativeFailValue > relativeFail) return 2;
        if (policy.WarnAboveAbsoluteIncrease is { } absoluteWarn && absolute > absoluteWarn) return 1;
        if (relative is { } relativeWarnValue && policy.WarnAboveRelativeIncrease is { } relativeWarn && relativeWarnValue > relativeWarn) return 1;
        return 0;
    }

    private static void ValidatePolicy(NumericPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policy.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy.Version);
        if (!IsDigest(policy.RulesDigest)) throw new ArgumentException("RulesDigest must be a SHA-256 digest.", nameof(policy));
        if (policy.WarnAboveAbsoluteIncrease is null && policy.FailAboveAbsoluteIncrease is null &&
            policy.WarnAboveRelativeIncrease is null && policy.FailAboveRelativeIncrease is null)
        {
            throw new ArgumentException("At least one numeric increase threshold must be provided.", nameof(policy));
        }
        if (policy.WarnAboveAbsoluteIncrease < 0 || policy.FailAboveAbsoluteIncrease < 0 ||
            policy.WarnAboveRelativeIncrease < 0 || policy.FailAboveRelativeIncrease < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Increase thresholds cannot be negative.");
        }
    }

    private static bool IsDigest(string value) => value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) && value[7..].All(Uri.IsHexDigit);

    private static bool TryParseDecimal(string? value, out decimal parsed) => decimal.TryParse(
        value,
        System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint,
        System.Globalization.CultureInfo.InvariantCulture,
        out parsed);

    private static string? Format(decimal? value) => value is null ? null : StructuredNumericComparator.Format(value.Value);

    private static AssessmentDocument CreateAssessment(
        string deltaId,
        string deltaStatus,
        PolicyProvenance policy,
        string outcome,
        IReadOnlyList<AssessmentFinding> findings,
        AssessmentProvenance provenance)
    {
        var projection = new
        {
            schema = AssessmentSchema,
            deltaId,
            deltaStatus,
            policy,
            outcome,
            findings,
            provenance
        };
        var projectionBytes = JsonSerializer.SerializeToUtf8Bytes(projection, SerializerOptions);
        var canonical = CanonicalJson.Canonicalize(projectionBytes);
        var framed = Encoding.UTF8.GetBytes("diffra/v1\0assessment\0").Concat(canonical).ToArray();
        var id = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(framed))}";
        return new AssessmentDocument(AssessmentSchema, id, deltaId, deltaStatus, policy, outcome, findings, provenance);
    }
}
