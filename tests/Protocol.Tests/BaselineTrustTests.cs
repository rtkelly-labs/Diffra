using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diffra.Protocol;

namespace Diffra.Protocol.Tests;

public static class BaselineTrustTests
{
    public static void RunAll()
    {
        GitBaselineResolverResolvesAndVerifiesMergeBase().GetAwaiter().GetResult();
        KeyedCollectionComparatorDistinguishesOrderedFromKeyed();
        KeyedCollectionComparatorDistinguishesNullMissingAndTypes();
        KeyedCollectionComparatorComparesDecodedPrimitives();
        KeyedCollectionComparatorComparesNumbersExactly();
        KeyedCollectionComparatorHandlesAwkwardInput();
        ComplexityComputesQuantilesAndDetectsOutliers();
        ComplexityEvaluatorEnforcesPolicy();
        ComplexityEvaluatorTreatsIncompatibleMeasurementsAsInconclusive();
        ComplexityEvaluatorUsesPolicyOutlierThreshold();
        Console.WriteLine("PASS Phase 3 baselines, Git merge-base resolver, keyed comparison, and complexity");
    }

    private static async Task GitBaselineResolverResolvesAndVerifiesMergeBase()
    {
        var repoRoot = FindRepositoryRoot();

        // Use HEAD and HEAD~1 (or HEAD and HEAD) in this repo
        var headCommit = CaptureGitOutput(repoRoot, "rev-parse", "HEAD");
        var resolved = await GitBaselineResolver.ResolveAsync(
            repoRoot: repoRoot,
            sourceRef: "HEAD",
            headCommit: headCommit,
            evidenceId: "sha256:1111111111111111111111111111111111111111111111111111111111111111",
            manifestDigest: "sha256:2222222222222222222222222222222222222222222222222222222222222222").ConfigureAwait(false);

        Equal(headCommit, resolved.ResolvedCommit);
        Equal("merge-base", resolved.Role);
        Equal("trusted", resolved.Status);
        DocumentIdentity.Verify("baseline-reference", resolved.BaselineReferenceBytes);
    }

    private static void KeyedCollectionComparatorDistinguishesOrderedFromKeyed()
    {
        // Reordering keyed collection: should be UNCHANGED
        var jsonBase = JsonNode.Parse("{\"items\": [{\"id\": \"a\", \"v\": 1}, {\"id\": \"b\", \"v\": 2}]}")!;
        var jsonReordered = JsonNode.Parse("{\"items\": [{\"id\": \"b\", \"v\": 2}, {\"id\": \"a\", \"v\": 1}]}")!;

        using var docBase = JsonDocument.Parse(jsonBase.ToJsonString());
        using var docReordered = JsonDocument.Parse(jsonReordered.ToJsonString());

        var keyedOptions = new KeyedCollectionOptions(ArrayComparisonMode.KeyedCollection, KeyProperty: "id");
        var changesKeyed = KeyedCollectionComparator.Compare(docBase.RootElement, docReordered.RootElement, keyedOptions);
        Equal(0, changesKeyed.Count); // Unchanged!

        // Same reordering with OrderedSequence: should be CHANGED
        var orderedOptions = new KeyedCollectionOptions(ArrayComparisonMode.OrderedSequence);
        var changesOrdered = KeyedCollectionComparator.Compare(docBase.RootElement, docReordered.RootElement, orderedOptions);
        if (changesOrdered.Count == 0)
        {
            throw new InvalidOperationException("Ordered sequence comparison should detect reordering as a change.");
        }
    }

    private static void KeyedCollectionComparatorDistinguishesNullMissingAndTypes()
    {
        var jsonBase = JsonNode.Parse("{\"existing\": \"val\", \"count\": 42, \"removed\": true}")!;
        var jsonCand = JsonNode.Parse("{\"existing\": \"val\", \"count\": \"42\", \"newNull\": null}")!;

        using var docBase = JsonDocument.Parse(jsonBase.ToJsonString());
        using var docCand = JsonDocument.Parse(jsonCand.ToJsonString());

        var changes = KeyedCollectionComparator.Compare(docBase.RootElement, docCand.RootElement);

        var countChange = changes.Single(c => c.Path == "/count");
        Equal("type_changed", countChange.Kind);

        var removedChange = changes.Single(c => c.Path == "/removed");
        Equal("removed", removedChange.Kind);

        var nullAdded = changes.Single(c => c.Path == "/newNull");
        Equal("added", nullAdded.Kind);
    }

    private static void KeyedCollectionComparatorComparesDecodedPrimitives()
    {
        // Parse the raw literals directly: a JsonNode round trip would already unescape them.
        using var plain = JsonDocument.Parse("{\"value\":\"a\",\"count\":1}");
        using var escaped = JsonDocument.Parse("{\"value\":\"\\u0061\",\"count\":1.0}");
        Equal(0, KeyedCollectionComparator.Compare(plain.RootElement, escaped.RootElement).Count);

        using var changed = JsonDocument.Parse("{\"value\":\"b\",\"count\":2}");
        var changes = KeyedCollectionComparator.Compare(plain.RootElement, changed.RootElement);
        Equal(2, changes.Count);
        if (!changes.All(c => c.Kind == "modified"))
        {
            throw new InvalidOperationException("Value changes should be reported as modified.");
        }
    }

    private static void KeyedCollectionComparatorComparesNumbersExactly()
    {
        // Parse the raw literals directly so each token keeps its original spelling.
        AssertNumbersEqual("1", "1.0");
        AssertNumbersEqual("1e2", "100");
        AssertNumbersEqual("-0", "0");
        AssertNumbersEqual("0.0e5", "0");
        AssertNumbersEqual("12.50", "1.25E1");

        // A decimal conversion would round 1e-29 to zero; exact comparison must not.
        using var zero = JsonDocument.Parse("{\"n\":0}");
        using var tiny = JsonDocument.Parse("{\"n\":1e-29}");
        var changes = KeyedCollectionComparator.Compare(zero.RootElement, tiny.RootElement);
        Equal(1, changes.Count);
        Equal("/n", changes[0].Path);
        Equal("modified", changes[0].Kind);

        using var positive = JsonDocument.Parse("{\"n\":1}");
        using var negative = JsonDocument.Parse("{\"n\":-1}");
        Equal(1, KeyedCollectionComparator.Compare(positive.RootElement, negative.RootElement).Count);
    }

    private static void AssertNumbersEqual(string baselineNumber, string candidateNumber)
    {
        using var baseline = JsonDocument.Parse($"{{\"n\":{baselineNumber}}}");
        using var candidate = JsonDocument.Parse($"{{\"n\":{candidateNumber}}}");
        var changes = KeyedCollectionComparator.Compare(baseline.RootElement, candidate.RootElement);
        if (changes.Count != 0)
        {
            throw new InvalidOperationException($"'{baselineNumber}' and '{candidateNumber}' should compare equal, but {changes.Count} change(s) were reported.");
        }
    }

    private static void KeyedCollectionComparatorHandlesAwkwardInput()
    {
        static IReadOnlyList<StructuralChange> Compare(string baseline, string candidate, ArrayComparisonMode mode = ArrayComparisonMode.KeyedCollection)
        {
            using var before = JsonDocument.Parse(baseline);
            using var after = JsonDocument.Parse(candidate);
            return KeyedCollectionComparator.Compare(before.RootElement, after.RootElement, new KeyedCollectionOptions(mode));
        }

        // Escape forms and -0 are the same value, so they are not changes.
        Equal(0, Compare("{\"s\":\"a/b\",\"n\":0}", "{\"s\":\"a\\/b\",\"n\":-0}").Count);

        // A key containing "/" is escaped as in RFC 6901 and cannot collide with a nested member.
        var paths = Compare("{\"x/y\":1,\"x\":{\"y\":1}}", "{\"x/y\":2,\"x\":{\"y\":2}}").Select(c => c.Path).Order(StringComparer.Ordinal).ToList();
        Equal("/x/y|/x~1y", string.Join("|", paths));

        // Non-string and duplicate ids fall back to ordered comparison instead of throwing.
        Equal(1, Compare("[{\"id\":1,\"v\":1}]", "[{\"id\":1,\"v\":2}]").Count);
        var duplicates = Compare("[{\"id\":\"a\",\"v\":1},{\"id\":\"a\",\"v\":2}]", "[{\"id\":\"a\",\"v\":2},{\"id\":\"a\",\"v\":1}]");
        Equal("[0]/v|[1]/v", string.Join("|", duplicates.Select(c => c.Path)));
    }

    private static void ComplexityComputesQuantilesAndDetectsOutliers()
    {
        var methods = new List<MethodComplexity>
        {
            new("MethodA", 2, 10),
            new("MethodB", 3, 15),
            new("MethodC", 5, 20),
            new("MethodD", 8, 30),
            new("MethodE", 18, 80), // Outlier (>= 15)
        };

        var payload = ComplexityPayload.Compute("diffra/core", "roslyn-analyzer", methods, outlierThreshold: 15);

        Equal(5, payload.Summary.TotalMethods);
        Equal(36, payload.Summary.TotalComplexity);
        Equal(7.2, payload.Summary.MeanComplexity);
        Equal(5.0, payload.Summary.Quantiles.P50);
        Equal(1, payload.Summary.Outliers.Count);
        Equal("MethodE", payload.Summary.Outliers[0].MethodId);

        // Verify JSON roundtrip
        var json = payload.ToJsonObject().ToJsonString();
        using var doc = JsonDocument.Parse(json);
        var roundtripped = ComplexityPayload.FromJsonElement(doc.RootElement);
        Equal(payload.Summary.MeanComplexity, roundtripped.Summary.MeanComplexity);
    }

    private static void ComplexityEvaluatorEnforcesPolicy()
    {
        var baseMethods = new List<MethodComplexity>
        {
            new("MethodA", 2, 10),
            new("MethodB", 4, 15),
        };
        var baseline = ComplexityPayload.Compute("diffra/core", "roslyn", baseMethods, outlierThreshold: 15);

        // Candidate adds a severe outlier
        var candMethods = new List<MethodComplexity>
        {
            new("MethodA", 2, 10),
            new("MethodB", 4, 15),
            new("MonsterMethod", 25, 120),
        };
        var candidate = ComplexityPayload.Compute("diffra/core", "roslyn", candMethods, outlierThreshold: 15);

        var delta = ComplexityEvaluator.Compare(baseline, candidate, outlierThreshold: 15);
        Equal(1, delta.NewOutliers.Count);
        Equal("MonsterMethod", delta.NewOutliers[0].MethodId);

        var policyStrict = new ComplexityPolicy(MaxMeanComplexity: 15.0, OutlierThreshold: 15, FailOnOutlierGrowth: true);
        var (outcome, findings) = ComplexityEvaluator.Evaluate(candidate, delta, policyStrict);

        Equal("fail", outcome);
        if (!findings.Any(f => f.Id == "complexity/new-outlier"))
        {
            throw new InvalidOperationException("Policy evaluation should have emitted new-outlier finding.");
        }
    }

    private static void ComplexityEvaluatorTreatsIncompatibleMeasurementsAsInconclusive()
    {
        var methods = new List<MethodComplexity> { new("MethodA", 2, 10) };
        var baseline = ComplexityPayload.Compute("diffra/core", "roslyn", methods);
        var candidate = ComplexityPayload.Compute("diffra/core", "lizard", methods);

        var delta = ComplexityEvaluator.Compare(baseline, candidate);
        Equal(false, delta.IsCompatible);

        var policy = new ComplexityPolicy(MaxMeanComplexity: 15.0, OutlierThreshold: 15, FailOnOutlierGrowth: true);
        var (outcome, findings) = ComplexityEvaluator.Evaluate(candidate, delta, policy);
        Equal("inconclusive", outcome);
        if (!findings.Any(f => f.Id == "complexity/incompatible" && f.Status == "inconclusive"))
        {
            throw new InvalidOperationException("Incompatible methodology must produce an inconclusive finding.");
        }

        var otherScope = ComplexityPayload.Compute("diffra/cli", "roslyn", methods);
        Equal(false, ComplexityEvaluator.Compare(baseline, otherScope).IsCompatible);
    }

    private static void ComplexityEvaluatorUsesPolicyOutlierThreshold()
    {
        var baseline = ComplexityPayload.Compute("diffra/core", "roslyn", [new MethodComplexity("MethodA", 9, 10)]);
        var candidate = ComplexityPayload.Compute("diffra/core", "roslyn", [new MethodComplexity("MethodA", 12, 10)]);

        // Compare runs with its default threshold of 15, so it records no new outlier itself.
        var delta = ComplexityEvaluator.Compare(baseline, candidate);
        Equal(0, delta.NewOutliers.Count);

        var policy = new ComplexityPolicy(MaxMeanComplexity: 100.0, OutlierThreshold: 10, FailOnOutlierGrowth: true);
        var (outcome, findings) = ComplexityEvaluator.Evaluate(candidate, delta, policy);
        Equal("fail", outcome);
        if (!findings.Any(f => f.Id == "complexity/new-outlier" && f.ChangePaths.Contains("MethodA")))
        {
            throw new InvalidOperationException("Policy threshold of 10 should flag MethodA crossing from 9 to 12.");
        }
    }

    private static string CaptureGitOutput(string repoRoot, params string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        using var proc = System.Diagnostics.Process.Start(startInfo)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return stdout.Trim();
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Diffra.slnx"))) return dir.FullName;
        }
        return Directory.GetCurrentDirectory();
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected: {expected}, Actual: {actual}");
        }
    }
}
