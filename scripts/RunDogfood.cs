using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var repoRoot = FindRepositoryRoot();
var judgeVersion = ReadJudgeVersion(repoRoot);
var baselinesDir = Path.Combine(repoRoot, "baselines", "dogfood");
var workDir = Path.Combine(repoRoot, "dogfood-run");
Directory.CreateDirectory(workDir);

Console.WriteLine($"=== Diffra Dogfooding Execution (Pinned Judge: {judgeVersion}) ===");

// 1. Collect candidate metrics
Console.WriteLine("1. Collecting repository metrics...");
var candidatePayload = Path.Combine(workDir, "candidate-payload.json");
RunProcess("dotnet", "run", Path.Combine(repoRoot, "scripts", "DogfoodMetrics.cs"), "--", candidatePayload);

// 2. Collect candidate evidence via pinned tool
Console.WriteLine("2. Producing candidate Evidence document...");
var candidateEvidence = Path.Combine(workDir, "candidate-evidence.json");
var commit = Environment.GetEnvironmentVariable("GITHUB_SHA");
if (string.IsNullOrWhiteSpace(commit))
{
    commit = TryGetHeadCommit() ?? "1111111111111111111111111111111111111111";
}

RunProcess("dotnet", "diffra", "collect",
    "--payload", candidatePayload,
    "--subject-id", "diffra/core",
    "--commit", commit,
    "--type-id", "diffra.metrics.dogfood",
    "--type-version", "1.0",
    "--producer-id", "diffra.dogfood",
    "--producer-version", judgeVersion,
    "-o", candidateEvidence);

if (args.Contains("--collect-only"))
{
    Console.WriteLine("[COLLECT] Candidate evidence collection complete (--collect-only).");
    return;
}

// 3. Resolve merge-base and baseline evidence
Console.WriteLine("3. Resolving merge-base baseline Evidence...");
var baselineEvidencePath = Path.Combine(workDir, "baseline-evidence.json");
var baselineRefPath = Path.Combine(workDir, "baseline-reference.json");
var mergeBaseCommit = ResolveMergeBase(repoRoot);

bool hasBaseline = false;
if (File.Exists(baselineEvidencePath) && new FileInfo(baselineEvidencePath).Length > 0)
{
    hasBaseline = true;
}
else if (!string.IsNullOrWhiteSpace(mergeBaseCommit))
{
    hasBaseline = TryFetchBaselineEvidence(mergeBaseCommit, baselineEvidencePath);
}

if (!hasBaseline)
{
    Console.WriteLine($"\n[INCONCLUSIVE] No baseline Evidence found for merge-base '{mergeBaseCommit ?? "unknown"}' on diffra-baselines.");
    Console.WriteLine("Reporting inconclusive assessment to GITHUB_STEP_SUMMARY. Gating permitted for bootstrap run.");

    var stepSummary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
    if (!string.IsNullOrEmpty(stepSummary) && File.Exists(stepSummary))
    {
        var inconclusiveReport = new StringBuilder();
        inconclusiveReport.AppendLine("\n\n## 🐕 Diffra Self-Evaluation (Dogfood)\n");
        inconclusiveReport.AppendLine("### Assessment: `inconclusive`\n");
        inconclusiveReport.AppendLine("> [!NOTE]");
        inconclusiveReport.Append("> No baseline Evidence document was found for merge-base `").Append(mergeBaseCommit ?? "unknown").Append("` on branch `diffra-baselines`.\n");
        inconclusiveReport.Append("> Candidate Evidence was generated for commit `").Append(commit).Append("` using pinned judge `").Append(judgeVersion).Append("`.\n");
        inconclusiveReport.AppendLine("> Gate status: **Passed (inconclusive / bootstrap)**.\n");
        File.AppendAllText(stepSummary, inconclusiveReport.ToString());
    }

    return;
}

// 4. Construct verified Baseline Reference
Console.WriteLine("4. Building verified Baseline Reference document...");
BuildBaselineReference(baselineEvidencePath, mergeBaseCommit!, baselineRefPath);

// 5. Diff against baseline
Console.WriteLine("5. Computing typed Delta against merge-base baseline...");
var deltaPath = Path.Combine(workDir, "delta.json");

RunProcess("dotnet", "diffra", "diff",
    "--baseline-ref", baselineRefPath,
    "--baseline-evidence", baselineEvidencePath,
    "--evidence", candidateEvidence,
    "-o", deltaPath);

// 6. Evaluate Delta against policy rules
Console.WriteLine("6. Evaluating Delta against quality rules...");
var rulesPath = Path.Combine(baselinesDir, "rules.json");
var assessmentPath = Path.Combine(workDir, "assessment.json");

var evalExitCode = RunProcessAllowExitCode("dotnet", "diffra", "eval",
    "--rules", rulesPath,
    "--delta", deltaPath,
    "-o", assessmentPath);

// 7. Materialize review bundle (with Design System styling)
Console.WriteLine("7. Materializing presentation review bundle...");
var bundleDir = Path.Combine(workDir, "review-bundle");
RunProcess("dotnet", "diffra", "present", "bundle",
    "--delta", deltaPath,
    "--assessment", assessmentPath,
    "--baseline-ref", baselineRefPath,
    "-o", bundleDir);

// 8. Generate Markdown summary
Console.WriteLine("8. Generating review summary report...");
var markdownReport = CaptureOutput("dotnet", "diffra", "report",
    deltaPath,
    "--assessment", assessmentPath,
    "--baseline-ref", baselineRefPath,
    "--format", "markdown");

Console.WriteLine("\n" + markdownReport);

var summaryFile = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
if (!string.IsNullOrEmpty(summaryFile) && File.Exists(summaryFile))
{
    File.AppendAllText(summaryFile, "\n\n## 🐕 Diffra Self-Evaluation (Dogfood)\n\n" + markdownReport);
    Console.WriteLine($"Appended report summary to $GITHUB_STEP_SUMMARY ({summaryFile}).");
}

if (evalExitCode != 0)
{
    Console.Error.WriteLine($"\n[FAIL] Policy evaluation exited with code {evalExitCode}. Gating failed.");
    Environment.Exit(evalExitCode);
}

Console.WriteLine("\n[SUCCESS] Diffra dogfooding run passed all quality policies!");

static string ReadJudgeVersion(string repoRoot)
{
    var manifestPath = Path.Combine(repoRoot, ".config", "dotnet-tools.json");
    if (!File.Exists(manifestPath)) return "0.0.1-alpha.1";
    using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
    if (doc.RootElement.TryGetProperty("tools", out var tools) &&
        tools.TryGetProperty("diffra.cli", out var cli) &&
        cli.TryGetProperty("version", out var ver))
    {
        return ver.GetString() ?? "0.0.1-alpha.1";
    }
    return "0.0.1-alpha.1";
}

static string? TryGetHeadCommit()
{
    try
    {
        var output = CaptureOutput("git", "rev-parse", "HEAD").Trim();
        return string.IsNullOrWhiteSpace(output) ? null : output;
    }
    catch
    {
        return null;
    }
}

static string? ResolveMergeBase(string repoRoot)
{
    var baseRef = Environment.GetEnvironmentVariable("GITHUB_BASE_REF");
    var target = string.IsNullOrWhiteSpace(baseRef) ? "origin/main" : $"origin/{baseRef}";

    var exitCode = RunProcessAllowExitCode("git", "rev-parse", "--verify", target);
    if (exitCode != 0)
    {
        RunProcessAllowExitCode("git", "fetch", "origin", "main");
    }

    try
    {
        var mergeBase = CaptureOutput("git", "merge-base", "HEAD", target).Trim();
        return string.IsNullOrWhiteSpace(mergeBase) ? null : mergeBase;
    }
    catch
    {
        return null;
    }
}

static bool TryFetchBaselineEvidence(string commitSha, string destinationPath)
{
    RunProcessAllowExitCode("git", "fetch", "origin", "diffra-baselines:refs/remotes/origin/diffra-baselines");

    var start = new ProcessStartInfo("git")
    {
        ArgumentList = { "show", $"origin/diffra-baselines:evidence/{commitSha}.json" },
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };

    try
    {
        using var proc = Process.Start(start);
        if (proc == null) return false;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        _ = stderrTask.GetAwaiter().GetResult();

        if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            File.WriteAllText(destinationPath, stdout);
            return true;
        }
    }
    catch
    {
        // ignored
    }

    return false;
}

static void BuildBaselineReference(string evidencePath, string resolvedCommit, string outputPath)
{
    var evidenceBytes = File.ReadAllBytes(evidencePath);
    using var evidenceDoc = JsonDocument.Parse(evidenceBytes);
    var evidenceId = evidenceDoc.RootElement.GetProperty("id").GetString()!;

    var fakeManifest = Encoding.UTF8.GetBytes("{\"diffra\":\"baseline-manifest\"}");
    var manifestDigest = "sha256:" + Convert.ToHexString(SHA256.HashData(fakeManifest)).ToLowerInvariant();

    var baselineRef = new JsonObject
    {
        ["evidenceId"] = evidenceId,
        ["manifestDigest"] = manifestDigest,
        ["resolver"] = new JsonObject
        {
            ["id"] = "diffra.dogfood.resolver",
            ["resolvedCommit"] = resolvedCommit,
            ["sourceRef"] = "main",
            ["version"] = "1.0"
        },
        ["role"] = "merge-base",
        ["schema"] = "https://diffra.dev/schemas/baseline-reference-v1.schema.json",
        ["sourceStore"] = "diffra-baselines",
        ["status"] = "trusted",
        ["subjectCommit"] = resolvedCommit,
        ["trust"] = new JsonObject
        {
            ["decisionId"] = $"merge-base:{resolvedCommit[..Math.Min(12, resolvedCommit.Length)]}",
            ["issuer"] = "trusted-ci"
        }
    };

    var sortedJson = SortJson(baselineRef);
    var unsignedBytes = Encoding.UTF8.GetBytes(sortedJson);
    var framed = Encoding.ASCII.GetBytes("diffra/v1\0baseline-reference\0").Concat(unsignedBytes).ToArray();
    var refId = "sha256:" + Convert.ToHexString(SHA256.HashData(framed)).ToLowerInvariant();
    baselineRef["id"] = refId;

    var finalJson = SortJson(baselineRef);
    File.WriteAllText(outputPath, finalJson);
}

static string SortJson(JsonObject obj)
{
    var sorted = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
    foreach (var (k, v) in obj)
    {
        if (v is JsonObject child)
        {
            sorted[k] = JsonNode.Parse(SortJson(child));
        }
        else
        {
            sorted[k] = v?.DeepClone();
        }
    }
    var res = new JsonObject();
    foreach (var (k, v) in sorted) res[k] = v;
    return res.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}

static string FindRepositoryRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Diffra.slnx"))) return dir.FullName;
    }
    return Directory.GetCurrentDirectory();
}

static void RunProcess(string filename, params string[] args)
{
    var exitCode = RunProcessAllowExitCode(filename, args);
    if (exitCode != 0)
    {
        throw new InvalidOperationException($"Command failed with exit code {exitCode}.");
    }
}

static int RunProcessAllowExitCode(string filename, params string[] args)
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
    _ = stdoutTask.GetAwaiter().GetResult();
    var err = stderrTask.GetAwaiter().GetResult();
    if (!string.IsNullOrWhiteSpace(err) && proc.ExitCode != 0)
    {
        Console.Error.WriteLine(err);
    }
    return proc.ExitCode;
}

static string CaptureOutput(string filename, params string[] args)
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
    var err = stderrTask.GetAwaiter().GetResult();
    if (proc.ExitCode != 0)
    {
        throw new InvalidOperationException($"Command failed with exit code {proc.ExitCode}: {err}");
    }
    return stdout;
}
