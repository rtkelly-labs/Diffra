using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diffra.Protocol;

namespace Diffra.Protocol.Tests;

/// <summary>Command-line integration checks for diff and eval.</summary>
public static class DiffEvalTests
{
    private const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public static void RunAll()
    {
        DiffAndEvalWriteVerifiedDocuments();
        UntrustedBaselineIsRejected();
        TamperedDeltaIsRejected();
        Console.WriteLine("PASS diff/eval CLI flow and validation");
    }

    private static void DiffAndEvalWriteVerifiedDocuments()
    {
        using var scratch = new ScratchDirectory();
        var baseline = WriteEvidence(scratch.FilePath("baseline.json"), "baseline-commit", 2);
        var candidate = WriteEvidence(scratch.FilePath("candidate.json"), "candidate-commit", 6);
        var baselineReferencePath = scratch.FilePath("baseline-reference.json");
        WriteBaselineReference(baselineReferencePath, baseline, "trusted");
        var deltaPath = scratch.FilePath("delta.json");

        var diff = RunCli("diff",
            "--baseline-ref", baselineReferencePath,
            "--baseline-evidence", baseline,
            "--evidence", candidate,
            "-o", deltaPath);
        Equal(0, diff.ExitCode, $"diff failed: {diff.StandardError}");
        True(File.Exists(deltaPath), "diff did not write a Delta.");

        var deltaBytes = File.ReadAllBytes(deltaPath);
        DocumentIdentity.Verify("delta", deltaBytes);
        using var delta = JsonDocument.Parse(deltaBytes);
        Equal("changed", delta.RootElement.GetProperty("status").GetString(), "Delta status is wrong.");
        var change = delta.RootElement.GetProperty("changes")[0];
        Equal("4", change.GetProperty("value").GetProperty("absolute").GetString(), "Absolute delta is wrong.");
        Equal("2", change.GetProperty("value").GetProperty("relative").GetString(), "Relative delta is wrong.");
        True(!delta.RootElement.TryGetProperty("verdict", out _), "Delta must not contain a policy verdict.");

        var warnRules = WriteRules(scratch.FilePath("warn.json"), "2", "10");
        var warnPath = scratch.FilePath("assessment-warn.json");
        var warn = RunCli("eval", "--rules", warnRules, "--delta", deltaPath, "-o", warnPath);
        Equal(0, warn.ExitCode, $"warn assessment should return 0: {warn.StandardError}");
        var warnAssessment = ReadAssessment(warnPath, "warn");

        var failRules = WriteRules(scratch.FilePath("fail.json"), "2", "3");
        var failPath = scratch.FilePath("assessment-fail.json");
        var fail = RunCli("eval", "--rules", failRules, "--delta", deltaPath, "-o", failPath);
        Equal(3, fail.ExitCode, $"failing assessment should return 3: {fail.StandardError}");
        var failAssessment = ReadAssessment(failPath, "fail");
        Equal(warnAssessment.DeltaId, failAssessment.DeltaId, "Changing rules must reuse the Delta.");
        True(warnAssessment.Id != failAssessment.Id, "Changing rules must produce a new Assessment identity.");
    }

    private static void UntrustedBaselineIsRejected()
    {
        using var scratch = new ScratchDirectory();
        var baseline = WriteEvidence(scratch.FilePath("baseline.json"), "baseline-commit", 1);
        var candidate = WriteEvidence(scratch.FilePath("candidate.json"), "candidate-commit", 2);
        var baselineReference = scratch.FilePath("baseline-reference.json");
        WriteBaselineReference(baselineReference, baseline, "candidate");
        var output = scratch.FilePath("delta.json");

        var result = RunCli("diff",
            "--baseline-ref", baselineReference,
            "--baseline-evidence", baseline,
            "--evidence", candidate,
            "-o", output);
        Equal(2, result.ExitCode, "Untrusted baseline reference should be rejected as invalid input.");
        True(!File.Exists(output), "An untrusted baseline must not produce an output file.");
    }

    private static void TamperedDeltaIsRejected()
    {
        using var scratch = new ScratchDirectory();
        var delta = JsonNode.Parse("""
            {
              "schema":"https://diffra.dev/schemas/delta-v1.schema.json",
              "id":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "status":"unchanged",
              "typeResolution":"recognized",
              "baselineId":"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
              "candidateEvidenceId":"sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
              "comparator":{"id":"cmp","version":"1.0","configDigest":"sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","inputTypeId":"org.example.metrics","inputTypeVersion":"1.0"},
              "changes":[],
              "provenance":{"baselineEvidenceId":"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","candidateEvidenceId":"sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","environment":{"runtime":".NET"}}
            }
            """)!;
        File.WriteAllText(scratch.FilePath("delta.json"), delta.ToJsonString());
        var rules = WriteRules(scratch.FilePath("rules.json"), "1", "2");
        var output = scratch.FilePath("assessment.json");

        var result = RunCli("eval", "--rules", rules, "--delta", scratch.FilePath("delta.json"), "-o", output);
        Equal(2, result.ExitCode, "Delta with an incorrect ID should be rejected.");
        True(!File.Exists(output), "Invalid Delta must not produce an Assessment.");
    }

    private static string WriteEvidence(string path, string commit, int complexity)
    {
        var payload = new JsonObject
        {
            ["scope"] = "public-api",
            ["methodology"] = "fixture-analyzer/1",
            ["metrics"] = new JsonObject { ["complexity"] = complexity }
        };
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var document = new JsonObject
        {
            ["schema"] = "https://diffra.dev/schemas/evidence-v1.schema.json",
            ["id"] = Digest,
            ["status"] = "produced",
            ["typeId"] = "org.example.metrics",
            ["typeVersion"] = "1.0",
            ["typeResolution"] = "recognized",
            ["subject"] = new JsonObject { ["kind"] = "git-repository", ["id"] = "example/project", ["commit"] = commit, ["repository"] = "example/project" },
            ["producer"] = new JsonObject { ["id"] = "fixture.metrics", ["version"] = "1.0", ["configDigest"] = Digest },
            ["environment"] = new JsonObject { ["platform"] = "test", ["runtime"] = ".NET", ["tools"] = new JsonArray() },
            ["provenance"] = new JsonObject { ["source"] = $"git:{commit}", ["artifactDigests"] = new JsonArray() },
            ["payloadDigest"] = EvidenceIdentity.ComputePayloadDigest(payloadBytes),
            ["payload"] = payload,
            ["artifactRefs"] = new JsonArray()
        };
        var bytesWithoutId = JsonSerializer.SerializeToUtf8Bytes(document);
        document["id"] = EvidenceIdentity.ComputeId(bytesWithoutId);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(document));
        return path;
    }

    private static void WriteBaselineReference(string path, string evidencePath, string status)
    {
        using var evidence = JsonDocument.Parse(File.ReadAllBytes(evidencePath));
        var commit = evidence.RootElement.GetProperty("subject").GetProperty("commit").GetString()!;
        var reference = new JsonObject
        {
            ["schema"] = "https://diffra.dev/schemas/baseline-reference-v1.schema.json",
            ["id"] = Digest,
            ["role"] = "merge-base",
            ["status"] = status,
            ["evidenceId"] = evidence.RootElement.GetProperty("id").GetString(),
            ["subjectCommit"] = commit,
            ["manifestDigest"] = Digest,
            ["resolver"] = new JsonObject { ["id"] = "fixture.resolver", ["version"] = "1.0", ["resolvedCommit"] = commit, ["sourceRef"] = "fixture" },
            ["sourceStore"] = "local-test-store",
            ["trust"] = new JsonObject { ["issuer"] = "fixture-ci", ["decisionId"] = "fixture-approval", ["decisionDigest"] = Digest }
        };
        reference["id"] = DocumentIdentity.ComputeId("baseline-reference", JsonSerializer.SerializeToUtf8Bytes(reference));
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(reference));
    }

    private static string WriteRules(string path, string warn, string fail)
    {
        var rules = new JsonObject
        {
            ["id"] = "org.example.complexity-policy",
            ["version"] = "1.0",
            ["warnAboveAbsoluteIncrease"] = warn,
            ["failAboveAbsoluteIncrease"] = fail
        };
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(rules));
        return path;
    }

    private static (string Id, string DeltaId) ReadAssessment(string path, string expectedOutcome)
    {
        var bytes = File.ReadAllBytes(path);
        DocumentIdentity.Verify("assessment", bytes);
        using var assessment = JsonDocument.Parse(bytes);
        Equal(expectedOutcome, assessment.RootElement.GetProperty("outcome").GetString(), "Assessment outcome is wrong.");
        return (assessment.RootElement.GetProperty("id").GetString()!, assessment.RootElement.GetProperty("deltaId").GetString()!);
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunCli(string command, params string[] args)
    {
        var root = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent?.Name ?? "Debug";
        var cli = Path.Combine(root, "src", "Cli", "bin", configuration, "net10.0", "Diffra.Cli.dll");
        if (!File.Exists(cli)) throw new FileNotFoundException("Build the CLI before running DiffEvalTests.", cli);

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(cli);
        start.ArgumentList.Add(command);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Diffra CLI process.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Diffra.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate Diffra.slnx from the current directory.");
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected <{expected}> but got <{actual}>.");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ScratchDirectory : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"diffra-diff-eval-{Guid.NewGuid():N}");
        public string FilePath(string name) => System.IO.Path.Combine(DirectoryPath, name);

        public ScratchDirectory() => Directory.CreateDirectory(DirectoryPath);

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
