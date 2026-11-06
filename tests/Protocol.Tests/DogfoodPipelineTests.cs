using System.Diagnostics;
using System.Text.Json;

namespace Diffra.Protocol.Tests;

public static class DogfoodPipelineTests
{
    public static void RunAll()
    {
        RunsDogfoodPipelineEndToEnd();
        Console.WriteLine("PASS dogfood pipeline runs and evaluates Diffra against baseline budget");
    }

    private static void RunsDogfoodPipelineEndToEnd()
    {
        var repoRoot = FindRepositoryRoot();
        var tempDir = Path.Combine(Path.GetTempPath(), "diffra-dogfood-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var baselinesDir = Path.Combine(repoRoot, "baselines", "dogfood");
            var baselineRef = Path.Combine(baselinesDir, "baseline-reference.json");
            var baselineEvidence = Path.Combine(baselinesDir, "baseline-evidence.json");
            var rulesPath = Path.Combine(baselinesDir, "rules.json");

            if (!File.Exists(baselineRef) || !File.Exists(baselineEvidence) || !File.Exists(rulesPath))
            {
                throw new InvalidOperationException("Dogfood baseline files are missing from baselines/dogfood.");
            }

            // 1. Collect candidate metrics via DogfoodMetrics.cs
            var candidatePayload = Path.Combine(tempDir, "payload.json");
            RunProcess("dotnet", "run", Path.Combine(repoRoot, "scripts", "DogfoodMetrics.cs"), "--", candidatePayload);

            var payloadBytes = File.ReadAllBytes(candidatePayload);
            using var payloadDoc = JsonDocument.Parse(payloadBytes);
            var metrics = payloadDoc.RootElement.GetProperty("metrics");
            var aspireCount = metrics.GetProperty("aspireRuntimeDependencies").GetInt32();
            if (aspireCount != 0)
            {
                throw new InvalidOperationException($"Aspire runtime dependencies must be 0, found {aspireCount}.");
            }

            // 2. CLI collect candidate evidence
            var cliDll = Path.Combine(repoRoot, "src", "Cli", "bin", "Release", "net10.0", "Diffra.Cli.dll");
            if (!File.Exists(cliDll))
            {
                // Fallback to Debug if Release not yet built
                var debugDll = Path.Combine(repoRoot, "src", "Cli", "bin", "Debug", "net10.0", "Diffra.Cli.dll");
                cliDll = File.Exists(debugDll) ? debugDll : cliDll;
            }

            var candidateEvidence = Path.Combine(tempDir, "evidence.json");
            RunProcess("dotnet", cliDll, "collect",
                "--payload", candidatePayload,
                "--subject-id", "diffra/core",
                "--commit", "2222222222222222222222222222222222222222",
                "--type-id", "diffra.metrics.dogfood",
                "--type-version", "1.0",
                "--producer-id", "diffra.dogfood",
                "--producer-version", "0.1.0",
                "-o", candidateEvidence);

            // 3. Diff against baseline
            var deltaPath = Path.Combine(tempDir, "delta.json");
            RunProcess("dotnet", cliDll, "diff",
                "--baseline-ref", baselineRef,
                "--baseline-evidence", baselineEvidence,
                "--evidence", candidateEvidence,
                "-o", deltaPath);

            // 4. Eval against rules
            var assessmentPath = Path.Combine(tempDir, "assessment.json");
            var evalCode = RunProcessAllowExitCode("dotnet", cliDll, "eval",
                "--rules", rulesPath,
                "--delta", deltaPath,
                "-o", assessmentPath);

            if (evalCode != 0)
            {
                throw new InvalidOperationException($"Dogfood policy evaluation returned exit code {evalCode}.");
            }

            using var assessmentDoc = JsonDocument.Parse(File.ReadAllBytes(assessmentPath));
            var outcome = assessmentDoc.RootElement.GetProperty("outcome").GetString();
            if (outcome != "pass")
            {
                throw new InvalidOperationException($"Expected pass assessment outcome, got {outcome}.");
            }

            // 5. Present bundle
            var reviewDir = Path.Combine(tempDir, "bundle");
            RunProcess("dotnet", cliDll, "present", "bundle",
                "--delta", deltaPath,
                "--assessment", assessmentPath,
                "--baseline-ref", baselineRef,
                "-o", reviewDir);

            if (!File.Exists(Path.Combine(reviewDir, "index.html")))
            {
                throw new InvalidOperationException("Review bundle index.html was not generated.");
            }

            if (!File.Exists(Path.Combine(reviewDir, "inventory.json")))
            {
                throw new InvalidOperationException("Review bundle inventory.json was not generated.");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Diffra.slnx"))) return dir.FullName;
        }
        return Directory.GetCurrentDirectory();
    }

    private static void RunProcess(string filename, params string[] args)
    {
        var exitCode = RunProcessAllowExitCode(filename, args);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Process {filename} exited with code {exitCode}.");
        }
    }

    private static int RunProcessAllowExitCode(string filename, params string[] args)
    {
        var start = new ProcessStartInfo(filename)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var proc = Process.Start(start)!;
        var err = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(err))
        {
            Console.Error.WriteLine(err);
        }
        return proc.ExitCode;
    }
}
