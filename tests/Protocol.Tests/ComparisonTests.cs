using System.Security.Cryptography;
using System.Text;
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
        AbsoluteFailOutranksZeroBaselineReview();
        ModifiedChangeWithoutAfterIsInconclusive();
        UnitChangesAreIncompatible();
        UnsupportedJsonValuesAreInconclusive();
        OpaqueEvidenceCannotPass();
        UntrustedBaselineCannotCompare();
        PolicyChangesReassessTheSameDelta();
        DeltaContainsNoPolicyVerdict();
        AddedAndRemovedMetricsAreReportedInPathOrder();
        IdenticalEvidenceIsUnchangedAndPasses();
        DeltaIdIsTheFramedCanonicalProjection();
        ComparisonGuardsReturnTheirStatus();
        MalformedMeasurementsAreInconclusive();
        RelativeThresholdsWarnAndFail();
        ZeroBaselineWithRelativePolicyNeedsReview();
        AssessmentRejectsUnassessableDeltas();
        InvalidArgumentsAreRejected();
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

    private static void AbsoluteFailOutranksZeroBaselineReview()
    {
        var policy = new NumericPolicy("org.example.numeric-policy", "1.0", RulesDigest,
            FailAboveAbsoluteIncrease: 10,
            FailAboveRelativeIncrease: 0.1m);

        var large = AssessmentEvaluator.Evaluate(Compare(Evidence("{\"count\":0}"), Evidence("{\"count\":100}", CandidateEvidenceId)), policy);
        Equal("fail", large.Findings.Single().Status);
        Equal("fail", large.Outcome);

        var small = AssessmentEvaluator.Evaluate(Compare(Evidence("{\"count\":0}"), Evidence("{\"count\":5}", CandidateEvidenceId)), policy);
        Equal("needs-review", small.Findings.Single().Status);
        Equal("needs-review", small.Outcome);
    }

    private static void ModifiedChangeWithoutAfterIsInconclusive()
    {
        var delta = Compare(Evidence("{\"count\":1}"), Evidence("{\"count\":2}", CandidateEvidenceId));
        var change = delta.Changes.Single();
        var withoutAfter = delta with { Changes = [change with { Value = change.Value with { After = null } }] };
        var assessment = AssessmentEvaluator.Evaluate(withoutAfter, Policy());

        Equal("inconclusive", assessment.Findings.Single().Status);
        Equal("inconclusive", assessment.Outcome);

        var unparseableAfter = delta with { Changes = [change with { Value = change.Value with { After = "two" } }] };
        Equal("inconclusive", AssessmentEvaluator.Evaluate(unparseableAfter, Policy()).Outcome);
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

    private static void AddedAndRemovedMetricsAreReportedInPathOrder()
    {
        var baseline = Evidence("{\"c\":1,\"a\":1,\"b\":{\"value\":2,\"unit\":\"ms\"},\"d\":5}");
        var candidate = Evidence("{\"d\":6,\"e\":{\"value\":3,\"unit\":\"ms\"},\"c\":2}", CandidateEvidenceId);
        var delta = Compare(baseline, candidate);

        Equal("changed", delta.Status);
        Equal("/a removed|/b removed|/c modified|/d modified|/e added", string.Join("|", delta.Changes.Select(change => $"{change.Path} {change.Kind}")));

        var removed = delta.Changes[1];
        Equal("2", removed.Value.Before);
        Equal(null, removed.Value.After);
        Equal(null, removed.Value.Absolute);
        Equal("ms", removed.Unit);

        var added = delta.Changes[4];
        Equal(null, added.Value.Before);
        Equal("3", added.Value.After);
        Equal("ms", added.Unit);

        var assessment = AssessmentEvaluator.Evaluate(delta, Policy());
        Equal("needs-review", assessment.Outcome);
        Equal("numeric-policy:/a needs-review|numeric-policy:/b needs-review|numeric-policy:/c pass|numeric-policy:/d pass|numeric-policy:/e needs-review",
            string.Join("|", assessment.Findings.Select(finding => $"{finding.Id} {finding.Status}")));
    }

    private static void IdenticalEvidenceIsUnchangedAndPasses()
    {
        var delta = Compare(Evidence("{\"a\":1,\"b\":{\"value\":2,\"unit\":\"ms\"}}"), Evidence("{\"b\":{\"unit\":\"ms\",\"value\":2},\"a\":1}", CandidateEvidenceId));
        Equal("unchanged", delta.Status);
        Equal(0, delta.Changes.Count);
        Equal(null, delta.Message);

        var assessment = AssessmentEvaluator.Evaluate(delta, Policy());
        Equal("pass", assessment.Outcome);
        Equal("numeric-policy pass", string.Join("|", assessment.Findings.Select(finding => $"{finding.Id} {finding.Status}")));
    }

    private static void DeltaIdIsTheFramedCanonicalProjection()
    {
        var delta = Compare(Evidence("{\"count\":2}"), Evidence("{\"count\":3}", CandidateEvidenceId));
        var projection = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = delta.Schema,
            status = delta.Status,
            typeResolution = delta.TypeResolution,
            baselineId = delta.BaselineId,
            candidateEvidenceId = delta.CandidateEvidenceId,
            comparator = delta.Comparator,
            changes = delta.Changes,
            provenance = delta.Provenance,
            message = delta.Message
        }, SerializerOptions);
        byte[] framed = [.. "diffra/v1\0delta\0"u8.ToArray(), .. CanonicalJson.Canonicalize(projection)];
        Equal($"sha256:{Convert.ToHexStringLower(SHA256.HashData(framed))}", delta.Id);

        // The id covers the comparison inputs, so a different candidate gives a different id.
        True(delta.Id != Compare(Evidence("{\"count\":2}"), Evidence("{\"count\":4}", CandidateEvidenceId)).Id);
    }

    private static void ComparisonGuardsReturnTheirStatus()
    {
        var baseline = Evidence("{\"count\":1}");
        var candidate = Evidence("{\"count\":2}", CandidateEvidenceId);

        var mismatched = StructuredNumericComparator.Compare(baseline, candidate, new BaselineEvidenceReference(BaselineId, CandidateEvidenceId, "trusted"));
        Equal("incompatible", mismatched.Status);
        Equal(BaselineId, mismatched.BaselineId);

        Equal("incompatible", Compare(baseline, candidate with { TypeId = "org.example.other" }).Status);

        var pending = Compare(baseline, candidate with { TypeResolution = "pending" });
        Equal("inconclusive", pending.Status);
        Equal("opaque", pending.TypeResolution);

        var blank = Compare(baseline with { Scope = " " }, candidate with { Scope = " " });
        Equal("inconclusive", blank.Status);
        True(blank.Message!.Contains("identity", StringComparison.Ordinal));
    }

    private static void MalformedMeasurementsAreInconclusive()
    {
        var baseline = Evidence("{\"count\":1}");
        foreach (var payload in new[]
        {
            "[1]",
            "{\"count\":{\"value\":1,\"unit\":\"\"}}",
            "{\"count\":{\"value\":1,\"extra\":2}}",
            "{\"count\":{\"value\":true}}",
            "{\"count\":\"1\"}",
        })
        {
            Equal("inconclusive", Compare(baseline, Evidence(payload, CandidateEvidenceId)).Status);
        }

        // Members are visited in ordinal order, so the first reported error is deterministic.
        var message = Compare(baseline, Evidence("{\"z\":\"bad\",\"a\":\"bad\"}", CandidateEvidenceId)).Message!;
        True(message.Contains("'/a'", StringComparison.Ordinal));

        // Escaped pointer segments: "~" and "/" in member names.
        var escaped = Compare(Evidence("{\"a/b\":{\"~c\":1}}"), Evidence("{\"a/b\":{\"~c\":2}}", CandidateEvidenceId));
        Equal("/a~1b/~0c", escaped.Changes.Single().Path);

        // A difference beyond decimal range overflows instead of throwing.
        var overflow = Compare(
            Evidence("{\"x\":{\"value\":\"-79228162514264337593543950335\"}}"),
            Evidence("{\"x\":{\"value\":\"79228162514264337593543950335\"}}", CandidateEvidenceId));
        Equal("inconclusive", overflow.Status);
        True(overflow.Message!.Contains("overflowed", StringComparison.Ordinal));
    }

    private static void RelativeThresholdsWarnAndFail()
    {
        // 10 -> 13 is an absolute increase of 3 and a relative increase of 0.3.
        var delta = Compare(Evidence("{\"count\":10}"), Evidence("{\"count\":13}", CandidateEvidenceId));
        Equal("0.3", delta.Changes.Single().Value.Relative);

        Equal("warn", Assess(delta, warnRelative: 0.2m, failRelative: 0.5m));
        Equal("fail", Assess(delta, warnRelative: 0.1m, failRelative: 0.25m));
        Equal("pass", Assess(delta, warnRelative: 0.3m));
        Equal("pass", Assess(delta, failRelative: 0.3m));
        Equal("fail", Assess(delta, warnAbsolute: 1m, failAbsolute: 2m, warnRelative: 1m));
        Equal("pass", Assess(delta, warnAbsolute: 3m, failAbsolute: 3m));

        // Budgets describe increases, so a decrease passes even with zero thresholds.
        var decrease = Compare(Evidence("{\"count\":10}"), Evidence("{\"count\":7}", CandidateEvidenceId));
        Equal("pass", Assess(decrease, warnAbsolute: 0m, failRelative: 0m));
    }

    private static void ZeroBaselineWithRelativePolicyNeedsReview()
    {
        var delta = Compare(Evidence("{\"count\":0}"), Evidence("{\"count\":4}", CandidateEvidenceId));

        var assessment = AssessmentEvaluator.Evaluate(delta, RelativePolicy(warnRelative: 0.1m));
        Equal("needs-review", assessment.Outcome);
        True(assessment.Findings.Single().Message.Contains("undefined", StringComparison.Ordinal));

        // Absolute-only policies can still judge a zero baseline.
        Equal("fail", Assess(delta, failAbsolute: 3m));
        Equal("pass", Assess(delta, warnAbsolute: 4m));
    }

    private static void AssessmentRejectsUnassessableDeltas()
    {
        var delta = Compare(Evidence("{\"count\":2}"), Evidence("{\"count\":6}", CandidateEvidenceId));

        var unknown = AssessmentEvaluator.Evaluate(delta with { Status = "mystery" }, Policy());
        Equal("inconclusive", unknown.Outcome);
        Equal("unknown-delta-status", unknown.Findings.Single().Id);

        var unreadable = delta with { Changes = [delta.Changes[0] with { Value = delta.Changes[0].Value with { Absolute = "n/a" } }] };
        var assessment = AssessmentEvaluator.Evaluate(unreadable, Policy());
        Equal("inconclusive", assessment.Outcome);
        Equal("inconclusive", assessment.Findings.Single().Status);

        var missingRelative = delta with { Changes = [delta.Changes[0] with { Value = delta.Changes[0].Value with { Relative = null } }] };
        Equal("inconclusive", AssessmentEvaluator.Evaluate(missingRelative, Policy()).Outcome);

        foreach (var status in new[] { "unavailable", "invalid", "incompatible", "inconclusive", "unsupported" })
        {
            var result = AssessmentEvaluator.Evaluate(delta with { Status = status }, Policy());
            Equal("inconclusive", result.Outcome);
            Equal("comparison-not-assessable", result.Findings.Single().Id);
        }
    }

    private static void InvalidArgumentsAreRejected()
    {
        var baseline = Evidence("{\"count\":1}");
        var candidate = Evidence("{\"count\":2}", CandidateEvidenceId);
        var reference = new BaselineEvidenceReference(BaselineId, BaselineEvidenceId, "trusted");
        var options = NumericComparatorOptions.Default;

        Throws<ArgumentNullException>(() => StructuredNumericComparator.Compare(null!, candidate, reference));
        Throws<ArgumentNullException>(() => StructuredNumericComparator.Compare(baseline, null!, reference));
        Throws<ArgumentNullException>(() => StructuredNumericComparator.Compare(baseline, candidate, null!));
        Throws<ArgumentException>(() => StructuredNumericComparator.Compare(baseline, candidate, reference, options with { Id = " " }));
        Throws<ArgumentException>(() => StructuredNumericComparator.Compare(baseline, candidate, reference, options with { Version = "" }));
        Throws<ArgumentNullException>(() => StructuredNumericComparator.Compare(baseline, candidate, reference, options with { Environment = null! }));
        foreach (var digest in BadDigests())
        {
            Throws<ArgumentException>(() => StructuredNumericComparator.Compare(baseline, candidate, reference, options with { ConfigDigest = digest }));
        }

        var delta = Compare(baseline, candidate);
        var policy = Policy();
        Throws<ArgumentNullException>(() => AssessmentEvaluator.Evaluate(null!, policy));
        Throws<ArgumentNullException>(() => AssessmentEvaluator.Evaluate(delta, null!));
        Throws<ArgumentException>(() => AssessmentEvaluator.Evaluate(delta, policy with { Id = " " }));
        Throws<ArgumentException>(() => AssessmentEvaluator.Evaluate(delta, policy with { Version = "" }));
        Throws<ArgumentException>(() => AssessmentEvaluator.Evaluate(delta, Policy(warnAbsolute: null)));
        foreach (var digest in BadDigests())
        {
            Throws<ArgumentException>(() => AssessmentEvaluator.Evaluate(delta, policy with { RulesDigest = digest }));
        }

        Throws<ArgumentOutOfRangeException>(() => AssessmentEvaluator.Evaluate(delta, Policy(warnAbsolute: -1)));
        Throws<ArgumentOutOfRangeException>(() => AssessmentEvaluator.Evaluate(delta, Policy(warnAbsolute: null, failAbsolute: -1)));
        Throws<ArgumentOutOfRangeException>(() => AssessmentEvaluator.Evaluate(delta, RelativePolicy(warnRelative: -0.1m)));
        Throws<ArgumentOutOfRangeException>(() => AssessmentEvaluator.Evaluate(delta, RelativePolicy(failRelative: -0.1m)));

        // Each threshold on its own is enough.
        _ = AssessmentEvaluator.Evaluate(delta, Policy(warnAbsolute: null, failAbsolute: 1));
        _ = AssessmentEvaluator.Evaluate(delta, RelativePolicy(warnRelative: 1));
        _ = AssessmentEvaluator.Evaluate(delta, RelativePolicy(failRelative: 1));
    }

    private static string[] BadDigests() =>
    [
        "sha256:" + new string('b', 63),
        "sha256:" + new string('b', 65),
        "sha512:" + new string('b', 64),
        "sha256:" + new string('g', 64),
    ];

    private static string Assess(
        DeltaDocument delta,
        decimal? warnAbsolute = null,
        decimal? failAbsolute = null,
        decimal? warnRelative = null,
        decimal? failRelative = null) =>
        AssessmentEvaluator.Evaluate(delta, new NumericPolicy("org.example.numeric-policy", "1.0", RulesDigest,
            warnAbsolute, failAbsolute, warnRelative, failRelative)).Outcome;

    private static NumericPolicy RelativePolicy(decimal? warnRelative = null, decimal? failRelative = null) =>
        new("org.example.numeric-policy", "1.0", RulesDigest,
            WarnAboveRelativeIncrease: warnRelative,
            FailAboveRelativeIncrease: failRelative);

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception.GetType() == typeof(TException))
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
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
