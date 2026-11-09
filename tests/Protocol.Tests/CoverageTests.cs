using System.Text.Json;
using Diffra.Protocol;

namespace Diffra.Protocol.Tests;

public static class CoverageTests
{
    public static void RunAll()
    {
        FormatsAndParsesCanonicalLineIntervals();
        SerializesAndDeserializesCoveragePayload();
        DetectsNewlyUncoveredLinesAndDeltas();
        EvaluatesCoveragePoliciesCorrectly();
        Console.WriteLine("PASS coverage payload, intervals, delta comparator, and policy evaluator");
    }

    private static void FormatsAndParsesCanonicalLineIntervals()
    {
        Equal(string.Empty, CoveragePayload.FormatLineRanges([]));
        Equal("14", CoveragePayload.FormatLineRanges([14]));
        Equal("14-16", CoveragePayload.FormatLineRanges([14, 15, 16]));
        Equal("10, 14-16, 25", CoveragePayload.FormatLineRanges([25, 15, 14, 16, 10, 15]));

        var parsed = CoveragePayload.ParseLineRanges("10, 14-16, 25");
        Equal(5, parsed.Count);
        if (!parsed.Contains(10) || !parsed.Contains(14) || !parsed.Contains(15) || !parsed.Contains(16) || !parsed.Contains(25))
        {
            throw new InvalidOperationException("Parsed intervals missing expected line numbers.");
        }
    }

    private static void SerializesAndDeserializesCoveragePayload()
    {
        var file1 = new FileCoverage(
            "src/Protocol/CanonicalJson.cs",
            CoverageCounter.Create(100, 95),
            CoverageCounter.Create(20, 18),
            "45, 88-90");

        var payload = new CoveragePayload(
            "diffra/protocol",
            "coverlet.collector",
            CoverageCounter.Create(100, 95),
            CoverageCounter.Create(20, 18),
            CoverageCounter.Create(10, 10),
            [file1]);

        var json = payload.ToJsonObject().ToJsonString();
        using var doc = JsonDocument.Parse(json);
        var roundtripped = CoveragePayload.FromJsonElement(doc.RootElement);

        Equal("diffra/protocol", roundtripped.Scope);
        Equal("coverlet.collector", roundtripped.Methodology);
        Equal(0.95, roundtripped.Lines.Rate);
        Equal(1, roundtripped.Files.Count);
        Equal("src/Protocol/CanonicalJson.cs", roundtripped.Files[0].Path);
        Equal("45, 88-90", roundtripped.Files[0].UncoveredLineRanges);
    }

    private static void DetectsNewlyUncoveredLinesAndDeltas()
    {
        var baseFile = new FileCoverage(
            "src/Protocol/CanonicalJson.cs",
            CoverageCounter.Create(100, 90),
            CoverageCounter.Create(20, 16),
            "10-15, 40-43");

        var baseline = new CoveragePayload(
            "diffra/protocol",
            "coverlet",
            CoverageCounter.Create(100, 90),
            CoverageCounter.Create(20, 16),
            CoverageCounter.Create(10, 9),
            [baseFile]);

        // Candidate has newly missed line 50 and line 51
        var candFile = new FileCoverage(
            "src/Protocol/CanonicalJson.cs",
            CoverageCounter.Create(110, 95),
            CoverageCounter.Create(20, 16),
            "10-15, 40-43, 50-51");

        var candidate = new CoveragePayload(
            "diffra/protocol",
            "coverlet",
            CoverageCounter.Create(110, 95),
            CoverageCounter.Create(20, 16),
            CoverageCounter.Create(11, 10),
            [candFile]);

        var delta = CoverageEvaluator.Compare(baseline, candidate);

        Equal(1, delta.NewlyUncoveredLines.Count);
        Equal("src/Protocol/CanonicalJson.cs", delta.NewlyUncoveredLines[0].Path);
        Equal("50-51", delta.NewlyUncoveredLines[0].FormattedRange);
    }

    private static void EvaluatesCoveragePoliciesCorrectly()
    {
        var baseline = new CoveragePayload(
            "diffra/core", "coverlet",
            CoverageCounter.Create(100, 90), CoverageCounter.Create(20, 16), CoverageCounter.Create(10, 9),
            []);

        var candidateGood = new CoveragePayload(
            "diffra/core", "coverlet",
            CoverageCounter.Create(100, 92), CoverageCounter.Create(20, 18), CoverageCounter.Create(10, 10),
            []);

        var deltaGood = CoverageEvaluator.Compare(baseline, candidateGood);
        var policyStrict = new CoveragePolicy(0.85, 0.0, true);

        var (outcomeGood, _) = CoverageEvaluator.Evaluate(candidateGood, deltaGood, policyStrict);
        Equal("pass", outcomeGood);

        // Test drop failure
        var candidateDropped = new CoveragePayload(
            "diffra/core", "coverlet",
            CoverageCounter.Create(100, 80), CoverageCounter.Create(20, 14), CoverageCounter.Create(10, 8),
            []);

        var deltaDropped = CoverageEvaluator.Compare(baseline, candidateDropped);
        var (outcomeDropped, findingsDropped) = CoverageEvaluator.Evaluate(candidateDropped, deltaDropped, policyStrict);
        Equal("fail", outcomeDropped);
        if (findingsDropped.Count == 0) throw new InvalidOperationException("Expected failure findings for dropped coverage.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected: {expected}, Actual: {actual}");
        }
    }
}
