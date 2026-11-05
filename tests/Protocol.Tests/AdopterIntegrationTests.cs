using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diffra.Examples.Adopters;
using Diffra.Protocol;

namespace Diffra.Protocol.Tests;

public static class AdopterIntegrationTests
{
    public static void RunAll()
    {
        AdaptsParquetSourceGeneratorGoldenOutputsToCanonicalEvidenceAndManifest();
        KeyedCollectionPreservesInvarianceOnReorderedApiMembers();
        DetectsAndRejectsBreakingApiModificationsAndRemovals();
        TracksEmittedSourceTextAndComplexityEvolution();
        VerifiesCompilationGatingIntegrity();
        ExportsAndImportsReleaseEvidenceBundleOffline();
        ExecutesExternalCommandProducerWithNode().GetAwaiter().GetResult();
        ExercisesBaselineCliCommands();
        Console.WriteLine("PASS Phase 5 adopter integration: Parquet.SourceGenerator slice, offline bundles, external producer, and baseline CLI");
    }

    private static void AdaptsParquetSourceGeneratorGoldenOutputsToCanonicalEvidenceAndManifest()
    {
        var repoRoot = FindRepositoryRoot();
        var baselineDir = Path.Combine(repoRoot, "examples", "adopters", "parquet-sourcegenerator", "fixtures", "baseline");
        var apiTxt = File.ReadAllText(Path.Combine(baselineDir, "PocoOrderParquetExtensions.api.txt"));
        var sourceCode = File.ReadAllText(Path.Combine(baselineDir, "PocoOrderParquetExtensions.g.cs"));

        var output = ParquetSourceGeneratorAdapter.Adapt(
            modelName: "PocoOrder",
            apiTxtContent: apiTxt,
            sourceCodeContent: sourceCode,
            commit: "1111111111111111111111111111111111111111");

        // 1. Verify all evidence envelopes pass strict protocol validation
        EvidenceIdentity.Verify(output.ApiEvidenceBytes);
        EvidenceIdentity.Verify(output.SourceEvidenceBytes);
        EvidenceIdentity.Verify(output.ComplexityEvidenceBytes);

        // 2. Verify Manifest document structure and SHA-256 identity
        using var manifestDoc = JsonDocument.Parse(output.ManifestBytes);
        var root = manifestDoc.RootElement;
        Equal("https://diffra.dev/schemas/manifest-v1.schema.json", root.GetProperty("schema").GetString());
        Equal(output.ManifestId, root.GetProperty("id").GetString());

        var groups = root.GetProperty("groups").EnumerateArray().ToList();
        Equal(1, groups.Count);
        Equal("group:golden-code-generation", groups[0].GetProperty("id").GetString());
        Equal("generated-source", groups[0].GetProperty("role").GetString());
        Equal(true, groups[0].GetProperty("required").GetBoolean());
        Equal("merge-base", groups[0].GetProperty("baselineRole").GetString());

        var artifacts = root.GetProperty("artifacts").EnumerateArray().ToList();
        Equal(2, artifacts.Count);
        Equal("text/x-csharp", artifacts[0].GetProperty("mediaType").GetString());
        Equal("text/plain", artifacts[1].GetProperty("mediaType").GetString());

        var evidenceIds = root.GetProperty("evidenceIds").EnumerateArray().Select(e => e.GetString()!).ToList();
        Equal(3, evidenceIds.Count);
        Equal(true, evidenceIds.Contains(output.ApiEvidenceId));
        Equal(true, evidenceIds.Contains(output.SourceEvidenceId));
        Equal(true, evidenceIds.Contains(output.ComplexityEvidenceId));
    }

    private static void KeyedCollectionPreservesInvarianceOnReorderedApiMembers()
    {
        var repoRoot = FindRepositoryRoot();
        var fixturesDir = Path.Combine(repoRoot, "examples", "adopters", "parquet-sourcegenerator", "fixtures");
        var baseApiTxt = File.ReadAllText(Path.Combine(fixturesDir, "baseline", "PocoOrderParquetExtensions.api.txt"));
        var candApiTxt = File.ReadAllText(Path.Combine(fixturesDir, "candidate", "PocoOrderParquetExtensions.api.txt"));

        var baseMembers = ParquetSourceGeneratorAdapter.ParseApiTxt(baseApiTxt);
        var candMembers = ParquetSourceGeneratorAdapter.ParseApiTxt(candApiTxt);

        var basePayload = new ApiSurfacePayload("model:PocoOrder", "roslyn-symbol-catalogue", baseMembers);
        var candPayload = new ApiSurfacePayload("model:PocoOrder", "roslyn-symbol-catalogue", candMembers);

        // Compare using typed API evaluator (backed by KeyedCollection semantics)
        var delta = ApiSurfaceEvaluator.Compare(basePayload, candPayload);

        // Even though candidate reordered members (lines swapped), Diffra correctly determines:
        // - ZERO members removed
        // - ZERO members modified
        // - Exactly ONE new member added (WriteParquetFastAsync)
        // - All baseline members remain unchanged
        Equal(0, delta.RemovedMembers.Count);
        Equal(0, delta.ModifiedMembers.Count);
        Equal(1, delta.AddedMembers.Count);
        Equal(baseMembers.Count, delta.UnchangedCount);
        Equal(false, delta.HasBreakingChanges);
        Equal("static SampleDomain.Models.PocoOrderParquetExtensions.WriteParquetFastAsync(this System.ReadOnlyMemory<byte> buffer, System.IO.Stream stream) -> System.Threading.Tasks.Task", delta.AddedMembers[0].Signature);

        // Evaluate against backwards-compatibility policy
        var policy = new ApiSurfacePolicy(DisallowBreakingChanges: true, DisallowRemovals: true, DisallowModifications: true);
        var (outcome, findings) = ApiSurfaceEvaluator.Evaluate(candPayload, delta, policy);

        Equal("pass", outcome);
        Equal(true, findings.Any(f => f.Id == "api/member-added" && f.Status == "pass"));
    }

    private static void DetectsAndRejectsBreakingApiModificationsAndRemovals()
    {
        var repoRoot = FindRepositoryRoot();
        var baselineApiTxt = File.ReadAllText(Path.Combine(repoRoot, "examples", "adopters", "parquet-sourcegenerator", "fixtures", "baseline", "PocoOrderParquetExtensions.api.txt"));
        var baseMembers = ParquetSourceGeneratorAdapter.ParseApiTxt(baselineApiTxt);
        var basePayload = new ApiSurfacePayload("model:PocoOrder", "roslyn-symbol-catalogue", baseMembers);

        // 1. Candidate with a removed member (breaking change)
        var breakingRemovedMembers = baseMembers.Where(m => !m.Id.Contains("WriteParquetBatchedAsync")).ToList();
        var removedPayload = new ApiSurfacePayload("model:PocoOrder", "roslyn-symbol-catalogue", breakingRemovedMembers);
        var removedDelta = ApiSurfaceEvaluator.Compare(basePayload, removedPayload);

        Equal(1, removedDelta.RemovedMembers.Count);
        Equal(true, removedDelta.HasBreakingChanges);

        var policy = new ApiSurfacePolicy(DisallowBreakingChanges: true, DisallowRemovals: true, DisallowModifications: true);
        var (removedOutcome, removedFindings) = ApiSurfaceEvaluator.Evaluate(removedPayload, removedDelta, policy);
        Equal("fail", removedOutcome);
        Equal(true, removedFindings.Any(f => f.Id == "api/member-removed" && f.Status == "fail"));

        // 2. Candidate with a modified signature / return type (breaking change)
        var modifiedMembers = baseMembers.Select(m =>
        {
            if (m.Id.Contains("From(System.IO.Stream stream)"))
            {
                return m with { ReturnType = "int", Signature = "static SampleDomain.Models.PocoOrderParquet.From(System.IO.Stream stream) -> int" };
            }
            return m;
        }).ToList();

        var modifiedPayload = new ApiSurfacePayload("model:PocoOrder", "roslyn-symbol-catalogue", modifiedMembers);
        var modDelta = ApiSurfaceEvaluator.Compare(basePayload, modifiedPayload);

        Equal(1, modDelta.ModifiedMembers.Count);
        Equal(true, modDelta.HasBreakingChanges);

        var (modOutcome, modFindings) = ApiSurfaceEvaluator.Evaluate(modifiedPayload, modDelta, policy);
        Equal("fail", modOutcome);
        Equal(true, modFindings.Any(f => f.Id == "api/member-modified" && f.Status == "fail"));
    }

    private static void TracksEmittedSourceTextAndComplexityEvolution()
    {
        var repoRoot = FindRepositoryRoot();
        var fixturesDir = Path.Combine(repoRoot, "examples", "adopters", "parquet-sourcegenerator", "fixtures");
        var baseSource = File.ReadAllText(Path.Combine(fixturesDir, "baseline", "PocoOrderParquetExtensions.g.cs"));
        var candSource = File.ReadAllText(Path.Combine(fixturesDir, "candidate", "PocoOrderParquetExtensions.g.cs"));

        var baseDigest = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseSource))).ToLowerInvariant();
        var candDigest = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candSource))).ToLowerInvariant();

        var baseText = new SourceTextPayload("PocoOrder.g.cs", "csharp", baseSource.Split('\n').Length, baseSource.Length, baseDigest);
        var candText = new SourceTextPayload("PocoOrder.g.cs", "csharp", candSource.Split('\n').Length, candSource.Length, candDigest);

        var textDelta = SourceTextEvaluator.Compare(baseText, candText);
        Equal(false, textDelta.IsContentIdentical);
        Equal(5, textDelta.LineCountDelta); // Added 5 lines for WriteParquetFastAsync
        Equal(candSource.Length - baseSource.Length, textDelta.CharacterCountDelta);

        // Complexity evaluation
        var baseMethods = ParquetSourceGeneratorAdapter.EstimateMethodComplexities("PocoOrder", baseSource);
        var candMethods = ParquetSourceGeneratorAdapter.EstimateMethodComplexities("PocoOrder", candSource);

        var baseComplexity = ComplexityPayload.Compute("model:PocoOrder", "cyclomatic-complexity", baseMethods, outlierThreshold: 20);
        var candComplexity = ComplexityPayload.Compute("model:PocoOrder", "cyclomatic-complexity", candMethods, outlierThreshold: 20);

        var compDelta = ComplexityEvaluator.Compare(baseComplexity, candComplexity, outlierThreshold: 25);
        var compPolicy = new ComplexityPolicy(MaxMeanComplexity: 30.0, OutlierThreshold: 25, FailOnOutlierGrowth: true);
        var (compOutcome, compFindings) = ComplexityEvaluator.Evaluate(candComplexity, compDelta, compPolicy);

        Equal("pass", compOutcome);
        Equal(true, compFindings.Count > 0);
    }

    private static void VerifiesCompilationGatingIntegrity()
    {
        var repoRoot = FindRepositoryRoot();
        var baselineDir = Path.Combine(repoRoot, "examples", "adopters", "parquet-sourcegenerator", "fixtures", "baseline");
        var apiTxt = File.ReadAllText(Path.Combine(baselineDir, "PocoOrderParquetExtensions.api.txt"));
        var sourceCode = File.ReadAllText(Path.Combine(baselineDir, "PocoOrderParquetExtensions.g.cs"));

        // Two identical runs at the same commit
        var run1 = ParquetSourceGeneratorAdapter.Adapt("PocoOrder", apiTxt, sourceCode, "3333333333333333333333333333333333333333");
        var run2 = ParquetSourceGeneratorAdapter.Adapt("PocoOrder", apiTxt, sourceCode, "3333333333333333333333333333333333333333");

        var approval = PromotionManager.VerifyAndApprove(
            run1.ApiEvidenceBytes,
            run2.ApiEvidenceBytes,
            headCommit: "3333333333333333333333333333333333333333",
            policyId: "policy:parquet-sourcegenerator-golden-gate",
            policyVersion: "1.0.0",
            rulesDigest: "sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
            issuer: "ryan-maintainer");

        Equal("https://diffra.dev/schemas/promotion-approval-v1.schema.json", approval.Schema);
        Equal("3333333333333333333333333333333333333333", approval.HeadCommit);
        Equal("ryan-maintainer", approval.Issuer);

        // Flaky or divergent runs must be rejected
        var divergentRun = ParquetSourceGeneratorAdapter.Adapt("PocoOrder", apiTxt + "\nstatic SampleDomain.Models.PocoOrderParquet.FlakyMutation() -> void", sourceCode, "3333333333333333333333333333333333333333");
        Throws<InvalidOperationException>(() => PromotionManager.VerifyAndApprove(
            run1.ApiEvidenceBytes,
            divergentRun.ApiEvidenceBytes,
            headCommit: "3333333333333333333333333333333333333333",
            policyId: "policy:parquet-sourcegenerator-golden-gate",
            policyVersion: "1.0.0",
            rulesDigest: "sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a"));
    }

    private static void ExportsAndImportsReleaseEvidenceBundleOffline()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "diffra-adopter-bundle-test-" + Guid.NewGuid().ToString("N"));
        var storePath = Path.Combine(tempRoot, "cas-store");
        var bundleTarPath = Path.Combine(tempRoot, "release-evidence-bundle.tar.gz");
        var importStorePath = Path.Combine(tempRoot, "imported-cas-store");

        Directory.CreateDirectory(storePath);
        Directory.CreateDirectory(importStorePath);

        try
        {
            var repoRoot = FindRepositoryRoot();
            var baselineDir = Path.Combine(repoRoot, "examples", "adopters", "parquet-sourcegenerator", "fixtures", "baseline");
            var apiTxt = File.ReadAllText(Path.Combine(baselineDir, "PocoOrderParquetExtensions.api.txt"));
            var sourceCode = File.ReadAllText(Path.Combine(baselineDir, "PocoOrderParquetExtensions.g.cs"));

            var adapted = ParquetSourceGeneratorAdapter.Adapt("PocoOrder", apiTxt, sourceCode, "5555555555555555555555555555555555555555");

            var manifestFile = Path.Combine(tempRoot, "manifest.json");
            File.WriteAllBytes(manifestFile, adapted.ManifestBytes);

            var artifactsDir = Path.Combine(tempRoot, "artifacts", "golden");
            Directory.CreateDirectory(artifactsDir);
            File.WriteAllText(Path.Combine(artifactsDir, "PocoOrderParquetExtensions.api.txt"), apiTxt);
            File.WriteAllText(Path.Combine(artifactsDir, "PocoOrderParquetExtensions.g.cs"), sourceCode);

            // Populate CAS with documents
            var cas = new ContentAddressedStore(storePath);
            cas.Put(adapted.ApiEvidenceBytes);
            cas.Put(adapted.SourceEvidenceBytes);
            cas.Put(adapted.ComplexityEvidenceBytes);
            cas.Put(adapted.ManifestBytes);

            // Export bundle
            var bundleOptions = new EvidenceBundle.ExportOptions(
                ManifestPath: manifestFile,
                OutputPath: bundleTarPath,
                StorePath: storePath,
                ArtifactsDirectory: tempRoot);
            EvidenceBundle.Export(bundleOptions);
            Equal(true, File.Exists(bundleTarPath));
            Equal(true, new FileInfo(bundleTarPath).Length > 0);

            // Import bundle offline into clean isolated store
            var importCas = new ContentAddressedStore(importStorePath);
            var importResult = EvidenceBundle.Import(bundleTarPath, importCas);

            var apiArtifactDigest = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiTxt))).ToLowerInvariant();
            var sourceDigest = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceCode))).ToLowerInvariant();

            Equal(adapted.ManifestId, importResult.ManifestId);
            Equal(2, importResult.ArtifactsCount);
            Equal(true, importCas.Contains(apiArtifactDigest));
            Equal(true, importCas.Contains(sourceDigest));

            // Verify content identity reproduced identically
            var importedApiBytes = importCas.Get(apiArtifactDigest);
            Equal(true, importedApiBytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(apiTxt)));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static async Task ExecutesExternalCommandProducerWithNode()
    {
        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "examples", "external-producer", "producer.mjs");

        var options = new ExternalCommandOptions(
            Executable: "node",
            Arguments: [scriptPath, "--scope", "test-node-scope"],
            ExpectedTypeId: "diffra.metrics.external",
            ExpectedTypeVersion: "1.0",
            SubjectId: "rtkelly-labs/Diffra",
            SubjectCommit: "6666666666666666666666666666666666666666");

        var result = await ExternalCommandAdapter.RunAsync(options).ConfigureAwait(false);

        Equal(0, result.ExitCode);
        Equal(true, !string.IsNullOrWhiteSpace(result.EvidenceId));
        Equal(true, !string.IsNullOrWhiteSpace(result.PayloadDigest));

        // Verify the resulting evidence envelope
        EvidenceIdentity.Verify(result.EvidenceBytes);

        using var doc = JsonDocument.Parse(result.EvidenceBytes);
        var root = doc.RootElement;
        Equal("diffra.metrics.external", root.GetProperty("typeId").GetString());
        Equal("6666666666666666666666666666666666666666", root.GetProperty("subject").GetProperty("commit").GetString());

        var payload = root.GetProperty("payload");
        Equal("test-node-scope", payload.GetProperty("scope").GetString());
        Equal("node-v25-runtime-telemetry", payload.GetProperty("methodology").GetString());
        Equal(true, payload.GetProperty("metrics").GetProperty("nodeMemoryHeapUsedBytes").GetInt64() > 0);
    }

    private static void ExercisesBaselineCliCommands()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "diffra-cli-baseline-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var repoRoot = FindRepositoryRoot();
            var baselineDir = Path.Combine(repoRoot, "examples", "adopters", "parquet-sourcegenerator", "fixtures", "baseline");
            var apiTxt = File.ReadAllText(Path.Combine(baselineDir, "PocoOrderParquetExtensions.api.txt"));
            var sourceCode = File.ReadAllText(Path.Combine(baselineDir, "PocoOrderParquetExtensions.g.cs"));

            var run1 = ParquetSourceGeneratorAdapter.Adapt("PocoOrder", apiTxt, sourceCode, "7777777777777777777777777777777777777777");
            var run2 = ParquetSourceGeneratorAdapter.Adapt("PocoOrder", apiTxt, sourceCode, "7777777777777777777777777777777777777777");

            var file1 = Path.Combine(tempDir, "run1.json");
            var file2 = Path.Combine(tempDir, "run2.json");
            var approvalOut = Path.Combine(tempDir, "approval.json");

            File.WriteAllBytes(file1, run1.ApiEvidenceBytes);
            File.WriteAllBytes(file2, run2.ApiEvidenceBytes);

            var cliDll = Path.Combine(repoRoot, "src", "Cli", "bin", "Release", "net10.0", "Diffra.Cli.dll");
            if (!File.Exists(cliDll))
            {
                var debugDll = Path.Combine(repoRoot, "src", "Cli", "bin", "Debug", "net10.0", "Diffra.Cli.dll");
                cliDll = File.Exists(debugDll) ? debugDll : cliDll;
            }

            // 1. diffra baseline verify
            var verifyCode = RunProcess("dotnet", cliDll,
                "baseline", "verify",
                "--evidence", file1,
                "--verification", file2,
                "--head-commit", "7777777777777777777777777777777777777777",
                "--policy-id", "policy:test",
                "--policy-version", "1.0",
                "--rules-digest", "sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
                "-o", approvalOut);

            Equal(0, verifyCode);
            Equal(true, File.Exists(approvalOut));

            // 2. diffra baseline update --mode missing
            var targetBase = Path.Combine(tempDir, "target-baseline.json");
            var updateMissingCode = RunProcess("dotnet", cliDll,
                "baseline", "update",
                "--mode", "missing",
                "--candidate", file1,
                "--baseline", targetBase,
                "-o", targetBase);
            Equal(0, updateMissingCode);
            Equal(true, File.Exists(targetBase));

            // 3. diffra baseline update --mode changed (no change since identical)
            var updateChangedCode = RunProcess("dotnet", cliDll,
                "baseline", "update",
                "--mode", "changed",
                "--candidate", file1,
                "--baseline", targetBase,
                "-o", targetBase);
            Equal(0, updateChangedCode);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static int RunProcess(string fileName, params string[] arguments)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        return proc.ExitCode;
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

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
    }
}
