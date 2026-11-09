using System.Text.Json;
using System.Text.Json.Serialization;
using Diffra.Protocol;

namespace Diffra.Protocol.Tests;

internal static class ComparisonTests
{
    private const string BaselineId = "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string BaselineEvidenceId = "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
    private const string CandidateEvidenceId = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RulesDigest = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void RunAll()
    {
        TypesScopeAndMethodologyMustMatch();
        NumericChangesIncludeAbsoluteAndRelativeValues();
        ZeroBaselineHasExplicitUndefinedRelativeValue();
        UnitChangesAreIncompatible();
        UnsupportedJsonValuesAreInconclusive();
        OpaqueEvidenceCannotPass();
        UntrustedBaselineCannotCompare();
        PolicyChangesReassessTheSameDelta();
        DeltaContainsNoPolicyVerdict();
    }

    private static void TypesScopeAndMethodologyMustMatch()
    {
        var baseline = Evidence("{\"complexity\":3}");
        var candidate = Evidence("{\"complexity\":5}", CandidateEvidenceId);
        Equal("changed", Compare(baseline, candidate).Status);

        Equal("incompatible", Compare(baseline, candidate with { TypeVersion = "2.0" }).Status);
        Equal("incompatible", Compare(baseline, candidate with { Scope = "all-methods" }).Status);
        Equal("incompatible", Compare(baseline, candidate with { Methodology = "different-tool" }).Status);
    }

    private static void NumericChangesIncludeAbsoluteAndRelativeValues()
    {
        var baseline = Evidence("{\"metrics\":{\"complexity\":2,\"nested\":{\"count\":8}}}");
        var candidate = Evidence("{\"metrics\":{\"complexity\":6,\"nested\":{\"count\":8}}}", CandidateEvidenceId);
        var delta = Compare(baseline, candidate);

        Equal("changed", delta.Status);
        Equal(1, delta.Changes.Count);
        Equal("/metrics/complexity", delta.Changes[0].Path);
        Equal("4", delta.Changes[0].Value.Absolute);
        Equal("2", delta.Changes[0].Value.Relative);
        Equal("defined", delta.Changes[0].Value.RelativeStatus);
        True(delta.Id.StartsWith("sha256:", StringComparison.Ordinal));
    }

    private static void ZeroBaselineHasExplicitUndefinedRelativeValue()
    {
        var baseline = Evidence("{\"count\":0}");
        var candidate = Evidence("{\"count\":4}", CandidateEvidenceId);
        var change = Compare(baseline, candidate).Changes.Single();

        Equal("4", change.Value.Absolute);
        Equal(null, change.Value.Relative);
        Equal("undefined-zero-baseline", change.Value.RelativeStatus);
    }

    private static void UnitChangesAreIncompatible()
    {
        var baseline = Evidence("{\"duration\":{\"value\":2,\"unit\":\"ms\"}}");
        var candidate = Evidence("{\"duration\":{\"value\":3,\"unit\":\"s\"}}", CandidateEvidenceId);
        var delta = Compare(baseline, candidate);

        Equal("incompatible", delta.Status);
        Equal(0, delta.Changes.Count);
    }

    private static void UnsupportedJsonValuesAreInconclusive()
    {
        var baseline = Evidence("{\"count\":1}");
        var candidate = Evidence("{\"count\":1.5}", CandidateEvidenceId);
        var delta = Compare(baseline, candidate);

        Equal("inconclusive", delta.Status);
        True(delta.Message!.Contains("canonical JSON v1", StringComparison.Ordinal));
        Equal("inconclusive", AssessmentEvaluator.Evaluate(delta, Policy()).Outcome);
    }

    private static void OpaqueEvidenceCannotPass()
    {
        var baseline = Evidence("{\"count\":1}");
        var candidate = Evidence("{\"count\":2}", CandidateEvidenceId) with { TypeResolution = "opaque" };
        var delta = Compare(baseline, candidate);

        Equal("unsupported", delta.Status);
        Equal("opaque", delta.TypeResolution);
        Equal("inconclusive", AssessmentEvaluator.Evaluate(delta, Policy()).Outcome);
    }

    private static void UntrustedBaselineCannotCompare()
    {
        var baseline = Evidence("{\"count\":1}");
        var candidate = Evidence("{\"count\":2}", CandidateEvidenceId);
        var delta = StructuredNumericComparator.Compare(
            baseline,
            candidate,
            new BaselineEvidenceReference(BaselineId, BaselineEvidenceId, "candidate"));

        Equal("inconclusive", delta.Status);
    }

    private static void PolicyChangesReassessTheSameDelta()
    {
        var baseline = Evidence("{\"count\":2}");
        var candidate = Evidence("{\"count\":6}", CandidateEvidenceId);
        var delta = Compare(baseline, candidate);
        var warnPolicy = Policy(warnAbsolute: 2, failAbsolute: 10, rulesDigest: RulesDigest);
        var failPolicy = Policy(warnAbsolute: 2, failAbsolute: 3,
            rulesDigest: "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");

        var warning = AssessmentEvaluator.Evaluate(delta, warnPolicy);
        var failure = AssessmentEvaluator.Evaluate(delta, failPolicy);

        Equal("warn", warning.Outcome);
        Equal("fail", failure.Outcome);
        Equal(delta.Id, warning.DeltaId);
        Equal(delta.Id, failure.DeltaId);
        True(warning.Id != failure.Id);
        Equal("changed", delta.Status);
        Equal("4", delta.Changes[0].Value.Absolute);
    }

    private static void DeltaContainsNoPolicyVerdict()
    {
        var delta = Compare(Evidence("{\"count\":0}"), Evidence("{\"count\":2}", CandidateEvidenceId));
        var json = JsonSerializer.Serialize(delta, SerializerOptions);
        True(!json.Contains("verdict", StringComparison.OrdinalIgnoreCase));
        True(json.Contains("\"relative\":null", StringComparison.Ordinal));
    }

    private static NumericEvidence Evidence(string payload, string id = BaselineEvidenceId)
    {
        using var document = JsonDocument.Parse(payload);
        return new NumericEvidence(id, "org.example.metrics", "1.0", "public-api", "example-analyzer/1", document.RootElement.Clone());
    }

    private static DeltaDocument Compare(NumericEvidence baseline, NumericEvidence candidate) =>
        StructuredNumericComparator.Compare(
            baseline,
            candidate,
            new BaselineEvidenceReference(BaselineId, BaselineEvidenceId, "trusted"));

    private static NumericPolicy Policy(decimal? warnAbsolute = 100, decimal? failAbsolute = null, string rulesDigest = RulesDigest) =>
        new("org.example.numeric-policy", "1.0", rulesDigest,
            WarnAboveAbsoluteIncrease: warnAbsolute,
            FailAboveAbsoluteIncrease: failAbsolute);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected <{expected}> but got <{actual}>.");
        }
    }

    private static void True(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Expected condition to be true.");
    }
}
