using System.Diagnostics;
using System.Text.Json;

namespace Diffra.Protocol.Tests;

public static class DogfoodPipelineTests
{
    public static void RunAll()
    {
        RunsDogfoodPipelineEndToEnd();
        Console.WriteLine("PASS dogfood pipeline runs and evaluates Diffra against baseline budget");

        ProvesInvariantViolationFails();
        Console.WriteLine("PASS invariant violation in dogfood metrics fails the self-evaluation gate");
    }

    private static void RunsDogfoodPipelineEndToEnd()
    {
        var repoRoot = FindRepositoryRoot();
        var tempDir = Path.Combine(Path.GetTempPath(), "diffra-dogfood-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var rulesPath = Path.Combine(repoRoot, "baselines", "dogfood", "rules.json");
            if (!File.Exists(rulesPath))
            {
                throw new InvalidOperationException("Quality rules file is missing from baselines/dogfood/rules.json.");
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

            // 3. Synthesize baseline evidence and verified reference in tempDir
            var baselineEvidence = Path.Combine(tempDir, "baseline-evidence.json");
            const string baselineCommit = "1111111111111111111111111111111111111111";
            RunProcess("dotnet", cliDll, "collect",
                "--payload", candidatePayload,
                "--subject-id", "diffra/core",
                "--commit", baselineCommit,
                "--type-id", "diffra.metrics.dogfood",
                "--type-version", "1.0",
                "--producer-id", "diffra.dogfood",
                "--producer-version", "0.1.0",
                "-o", baselineEvidence);

            var evidenceBytes = File.ReadAllBytes(baselineEvidence);
            using var evidenceDoc = JsonDocument.Parse(evidenceBytes);
            var evidenceId = evidenceDoc.RootElement.GetProperty("id").GetString()!;
            var manifestDigest = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(evidenceBytes));

            var baselineRefObj = new System.Text.Json.Nodes.JsonObject
            {
                ["evidenceId"] = evidenceId,
                ["manifestDigest"] = manifestDigest,
                ["resolver"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = "diffra.dogfood.resolver",
                    ["resolvedCommit"] = baselineCommit,
                    ["sourceRef"] = "main",
                    ["version"] = "1.0"
                },
                ["role"] = "merge-base",
                ["schema"] = "https://diffra.net/schemas/baseline-reference-v1.schema.json",
                ["sourceStore"] = "diffra-baselines",
                ["status"] = "trusted",
                ["subjectCommit"] = baselineCommit,
                ["trust"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["decisionId"] = $"merge-base:{baselineCommit[..12]}",
                    ["issuer"] = "trusted-ci"
                }
            };

            var baselineRefBytes = System.Text.Encoding.UTF8.GetBytes(baselineRefObj.ToJsonString());
            var refId = DocumentIdentity.ComputeId("baseline-reference", baselineRefBytes);
            baselineRefObj["id"] = refId;
            var baselineRef = Path.Combine(tempDir, "baseline-reference.json");
            File.WriteAllBytes(baselineRef, CanonicalJson.Canonicalize(System.Text.Encoding.UTF8.GetBytes(baselineRefObj.ToJsonString())));

            // 4. Diff against baseline
            var deltaPath = Path.Combine(tempDir, "delta.json");
            RunProcess("dotnet", cliDll, "diff",
                "--baseline-ref", baselineRef,
                "--baseline-evidence", baselineEvidence,
                "--evidence", candidateEvidence,
                "-o", deltaPath);

            // 5. Eval against rules
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
            if (outcome != "pass" && outcome != "warn")
            {
                throw new InvalidOperationException($"Expected non-failing assessment outcome (pass or warn), got {outcome}.");
            }

            // 6. Present bundle
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

    private static void ProvesInvariantViolationFails()
    {
        var repoRoot = FindRepositoryRoot();
        var tempDir = Path.Combine(Path.GetTempPath(), "diffra-invariant-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var runDogfoodScript = Path.Combine(repoRoot, "scripts", "RunDogfood.cs");

            // 1. Valid invariants pass and --check-invariants does not create dogfood-run/
            var dogfoodRunDir = Path.Combine(repoRoot, "dogfood-run");
            bool dogfoodRunExistedBefore = Directory.Exists(dogfoodRunDir);

            var validPayload = Path.Combine(tempDir, "valid-payload.json");
            File.WriteAllText(validPayload, """
            {
              "scope": "diffra/core",
              "methodology": "static-analysis",
              "metrics": {
                "aspireRuntimeDependencies": 0,
                "packageSizeBytes": 184000,
                "schemasCount": 12,
                "sourceFilesCount": 29
              }
            }
            """);

            var (validExit, validOut, _) = RunProcessAllowExitCodeWithOutput("dotnet", "run", runDogfoodScript, "--", "--check-invariants", validPayload);
            if (validExit != 0)
            {
                throw new InvalidOperationException($"Expected valid invariants to pass (exit 0), got {validExit}:\n{validOut}");
            }
            if (!validOut.Contains("[INVARIANT] aspireRuntimeDependencies=0 PASS"))
            {
                throw new InvalidOperationException($"Expected PASS outputs for invariants, got:\n{validOut}");
            }
            if (!dogfoodRunExistedBefore && Directory.Exists(dogfoodRunDir))
            {
                throw new InvalidOperationException("--check-invariants created dogfood-run/ directory unexpectedly.");
            }

            // 2. Aspire dependency invariant violation fails
            var badAspirePayload = Path.Combine(tempDir, "bad-aspire-payload.json");
            File.WriteAllText(badAspirePayload, """
            {
              "scope": "diffra/core",
              "methodology": "static-analysis",
              "metrics": {
                "aspireRuntimeDependencies": 1,
                "packageSizeBytes": 184000,
                "schemasCount": 12,
                "sourceFilesCount": 29
              }
            }
            """);

            var (badAspireExit, badAspireOut, badAspireErr) = RunProcessAllowExitCodeWithOutput("dotnet", "run", runDogfoodScript, "--", "--check-invariants", badAspirePayload);
            if (badAspireExit == 0)
            {
                throw new InvalidOperationException("Expected non-zero exit code when aspireRuntimeDependencies > 0, but got 0.");
            }
            if (!badAspireErr.Contains("[INVARIANT] aspireRuntimeDependencies=1 FAIL (expected 0)"))
            {
                throw new InvalidOperationException($"Expected FAIL message in stderr for aspireRuntimeDependencies, got:\n{badAspireErr}\n{badAspireOut}");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup of temporary test directory; ignore transient file locks during teardown.
                }
                catch (UnauthorizedAccessException)
                {
                    // Best-effort cleanup of temporary test directory; ignore permission errors during teardown.
                }
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
        var (exitCode, _, stderr) = RunProcessAllowExitCodeWithOutput(filename, args);
        if (exitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
        {
            Console.Error.WriteLine(stderr);
        }
        return exitCode;
    }

    private static (int exitCode, string stdout, string stderr) RunProcessAllowExitCodeWithOutput(string filename, params string[] args)
    {
        var start = new ProcessStartInfo(filename)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var proc = Process.Start(start)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        return (proc.ExitCode, stdout, stderr);
    }
}
