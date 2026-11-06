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
        ComplexityComputesQuantilesAndDetectsOutliers();
        ComplexityEvaluatorEnforcesPolicy();
        PromotionRequiresIdenticalTwoRunVerification();
        PromotionInvalidatesOnStaleIdentities();
        Console.WriteLine("PASS Phase 3 baselines, Git merge-base resolver, keyed comparison, complexity, and promotion");
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

    private static void PromotionRequiresIdenticalTwoRunVerification()
    {
        var commit = "5555555555555555555555555555555555555555";
        var payloadBytes = Encoding.UTF8.GetBytes("{\"deterministic\": true}");
        var canonical = CanonicalJson.Canonicalize(payloadBytes);

        var (_, _, run1) = EvidenceDocumentBuilder.Build("diffra.metric", "1.0", "core", commit, "prod", "1.0", canonical, "macOS", "10.0");
        var (_, _, run2) = EvidenceDocumentBuilder.Build("diffra.metric", "1.0", "core", commit, "prod", "1.0", canonical, "macOS", "10.0");

        var approval = PromotionManager.VerifyAndApprove(run1, run2, commit, "policy.v1", "1.0", "sha256:rules123");
        Equal(commit, approval.HeadCommit);
        Equal("policy.v1", approval.PolicyId);

        // Test flaky run rejection
        var divergentPayload = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes("{\"deterministic\": false}"));
        var (_, _, runDivergent) = EvidenceDocumentBuilder.Build("diffra.metric", "1.0", "core", commit, "prod", "1.0", divergentPayload, "macOS", "10.0");

        try
        {
            PromotionManager.VerifyAndApprove(run1, runDivergent, commit, "policy.v1", "1.0", "sha256:rules123");
            throw new InvalidOperationException("Promotion should have failed due to divergent runs.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Flaky observation", StringComparison.Ordinal))
        {
            // Expected
        }
    }

    private static void PromotionInvalidatesOnStaleIdentities()
    {
        var commit = "6666666666666666666666666666666666666666";
        var payloadBytes = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes("{\"ok\": true}"));
        var (_, _, run1) = EvidenceDocumentBuilder.Build("diffra.metric", "1.0", "core", commit, "prod", "1.0", payloadBytes, "macOS", "10.0");

        var approval = PromotionManager.VerifyAndApprove(run1, run1, commit, "policy.v1", "1.0", "sha256:rules123");

        // Validate against matching evidence -> Valid
        var (isValid, _) = PromotionManager.ValidateApproval(approval, run1, "policy.v1", "sha256:rules123");
        Equal(true, isValid);

        // Validate against altered policy -> Invalid
        var (isPolicyStale, reason) = PromotionManager.ValidateApproval(approval, run1, "policy.v1", "sha256:newRules");
        Equal(false, isPolicyStale);
        if (string.IsNullOrEmpty(reason)) throw new InvalidOperationException("Expected invalidation reason for altered policy.");
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
