using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length >= 2 && args[0] == "--check-invariants")
{
    var testPayload = args[1];
    CheckInvariants(testPayload);
    return;
}

var repoRoot = FindRepositoryRoot();
var judgeVersion = ReadJudgeVersion(repoRoot);
var baselinesDir = Path.Combine(repoRoot, "baselines", "dogfood");
var workDir = Path.Combine(repoRoot, "dogfood-run");
Directory.CreateDirectory(workDir);

// Guard: A PR must not supply tracked baseline or dogfood run files.
var trackedFiles = CaptureOutput("git", "ls-files", "dogfood-run").Trim();
if (!string.IsNullOrWhiteSpace(trackedFiles))
{
    Console.Error.WriteLine($"[FAIL] Tracked files found under dogfood-run/:\n{trackedFiles}\nA PR must not supply tracked baseline or dogfood run files.");
    Environment.Exit(1);
    return;
}

Console.WriteLine($"=== Diffra Dogfooding Execution (Pinned Judge: {judgeVersion}) ===");

// 1. Collect candidate metrics
Console.WriteLine("1. Collecting repository metrics...");
var candidatePayload = Path.Combine(workDir, "candidate-payload.json");
RunProcess("dotnet", "run", Path.Combine(repoRoot, "scripts", "DogfoodMetrics.cs"), "--", candidatePayload);

// Check hard architectural invariants
CheckInvariants(candidatePayload);

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
    if (!File.Exists(candidateEvidence) || new FileInfo(candidateEvidence).Length == 0)
    {
        Console.Error.WriteLine($"[FAIL] Candidate evidence file was not created or is empty: {candidateEvidence}");
        Environment.Exit(1);
        return;
    }

    try
    {
        using var collectDoc = JsonDocument.Parse(File.ReadAllBytes(candidateEvidence));
        var collectRoot = collectDoc.RootElement;
        if (!collectRoot.TryGetProperty("id", out _) ||
            !collectRoot.TryGetProperty("subject", out var s) || !s.TryGetProperty("commit", out _) ||
            !collectRoot.TryGetProperty("payload", out _))
        {
            throw new JsonException("Candidate evidence document is missing required schema fields.");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[FAIL] Malformed candidate evidence document: {ex.Message}");
        Environment.Exit(1);
        return;
    }

    Console.WriteLine("[COLLECT] Candidate evidence collection complete and verified (--collect-only).");
    return;
}

// 3. Resolve merge-base and baseline evidence
Console.WriteLine("3. Resolving merge-base baseline Evidence...");
var baselineEvidencePath = Path.Combine(workDir, "baseline-evidence.json");
var baselineRefPath = Path.Combine(workDir, "baseline-reference.json");

// Never trust pre-existing baseline files or derived outputs from an earlier run;
// remove them before resolution so an inconclusive run cannot leave a stale report visible.
foreach (var stalePath in new[] { baselineEvidencePath, baselineRefPath, Path.Combine(workDir, "delta.json"), Path.Combine(workDir, "assessment.json") })
{
    if (File.Exists(stalePath)) File.Delete(stalePath);
}

var staleBundleDir = Path.Combine(workDir, "review-bundle");
if (Directory.Exists(staleBundleDir)) Directory.Delete(staleBundleDir, recursive: true);

// Always compute merge-base against origin/main (never GITHUB_BASE_REF) so stacked PRs work
var mergeBaseCommit = ResolveMergeBaseStrict(repoRoot);
Console.WriteLine($"Resolved merge-base against origin/main: {mergeBaseCommit}");

const string baselinesBranch = "diffra-baselines";

// Check whether baseline branch exists on origin
var (lsRemoteExit, lsRemoteOut, lsRemoteErr) = RunProcessAllowExitCodeWithOutput("git", "ls-remote", "--heads", "origin", baselinesBranch);
if (lsRemoteExit != 0)
{
    Console.Error.WriteLine($"[FAIL] Network or git error querying remote '{baselinesBranch}' branch (exit code {lsRemoteExit}): {lsRemoteErr}");
    Environment.Exit(lsRemoteExit);
    return;
}

var stepSummary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");

if (string.IsNullOrWhiteSpace(lsRemoteOut))
{
    // Case (a): diffra-baselines branch does not exist yet (bootstrap)
    Console.WriteLine($"\n[INCONCLUSIVE] Case (a): '{baselinesBranch}' branch does not exist yet on remote (bootstrap).");
    Console.WriteLine("Reporting inconclusive assessment to GITHUB_STEP_SUMMARY. Gating permitted for bootstrap run.");

    if (!string.IsNullOrEmpty(stepSummary) && File.Exists(stepSummary))
    {
        var inconclusiveReport = new StringBuilder();
        inconclusiveReport.AppendLine("\n\n## 🐕 Diffra Self-Evaluation (Dogfood)\n");
        inconclusiveReport.AppendLine("### Assessment: `inconclusive`\n");
        inconclusiveReport.AppendLine("> [!NOTE]");
        inconclusiveReport.Append("> Case (a): `").Append(baselinesBranch).Append("` branch does not exist yet on remote (bootstrap).\n");
        inconclusiveReport.Append("> Candidate Evidence was generated for commit `").Append(commit).Append("` using pinned judge `").Append(judgeVersion).Append("`.\n");
        inconclusiveReport.AppendLine("> Gate status: **Passed (inconclusive / bootstrap)**.\n");
        File.AppendAllText(stepSummary, inconclusiveReport.ToString());
    }

    return;
}

// Branch exists on remote: fetch it fresh
var fetchExit = RunProcessAllowExitCode("git", "fetch", "origin", $"+refs/heads/{baselinesBranch}:refs/remotes/origin/{baselinesBranch}");
if (fetchExit != 0)
{
    Console.Error.WriteLine($"[FAIL] Failed to fetch origin/{baselinesBranch} (exit code {fetchExit}).");
    Environment.Exit(fetchExit);
    return;
}

// The comparison point is the merge base. When it has no record, use the nearest recorded
// first-parent ancestor inside the history window and state the distance, because the changes
// merged in between are then part of the comparison (docs/adr/0003-comparison-history-and-noise.md).
// The window is fixed here until the root file provides it as a setting.
const int HistoryWindowCommits = 20;
var ancestorList = CaptureOutput("git", "rev-list", "--first-parent", "-n", HistoryWindowCommits.ToString(System.Globalization.CultureInfo.InvariantCulture), mergeBaseCommit)
    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

string? matchedCommit = null;
var matchedDistance = 0;
foreach (var sha in ancestorList)
{
    var (showExit, showOut, showErr) = RunProcessAllowExitCodeWithOutput("git", "show", $"origin/{baselinesBranch}:evidence/{sha}.json");
    if (showExit == 0)
    {
        if (string.IsNullOrWhiteSpace(showOut))
        {
            Console.Error.WriteLine($"[FAIL] Baseline Evidence document for commit '{sha}' at 'origin/{baselinesBranch}:evidence/{sha}.json' is empty.");
            Environment.Exit(1);
            return;
        }

        try
        {
            using var parsedDoc = JsonDocument.Parse(showOut);
            var root = parsedDoc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("id", out var idElem) || idElem.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("subject", out var subjElem) || subjElem.ValueKind != JsonValueKind.Object ||
                !subjElem.TryGetProperty("commit", out var commitElem) || commitElem.ValueKind != JsonValueKind.String)
            {
                throw new JsonException("Baseline evidence document is missing required schema fields.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] Malformed baseline Evidence document at 'origin/{baselinesBranch}:evidence/{sha}.json': {ex.Message}");
            Environment.Exit(1);
            return;
        }

        File.WriteAllText(baselineEvidencePath, showOut);
        matchedCommit = sha;
        matchedDistance = Array.IndexOf(ancestorList, sha);
        break;
    }
    else
    {
        // Only "path does not exist at that commit" counts as missing
        bool isNotFound = showErr.Contains("does not exist in", StringComparison.OrdinalIgnoreCase) ||
                          showErr.Contains("exists on disk, but not in", StringComparison.OrdinalIgnoreCase);

        if (!isNotFound)
        {
            Console.Error.WriteLine($"[FAIL] Error reading 'origin/{baselinesBranch}:evidence/{sha}.json' (exit {showExit}): {showErr.Trim()}");
            Environment.Exit(showExit != 0 ? showExit : 1);
            return;
        }
    }
}

if (matchedCommit == null)
{
    // Case (b): nothing in the history window has a record, so the change cannot be attributed
    Console.WriteLine($"::warning::Cannot attribute: no record in the history window of {HistoryWindowCommits} first-parent commits ending at the merge base.");
    Console.WriteLine($"\n[INCONCLUSIVE] Case (b): Cannot attribute. No record on {baselinesBranch} in the history window of {HistoryWindowCommits} first-parent commits ending at merge-base '{mergeBaseCommit}'.");
    Console.WriteLine("Reporting inconclusive assessment to GITHUB_STEP_SUMMARY. No comparison was made.");

    if (!string.IsNullOrEmpty(stepSummary) && File.Exists(stepSummary))
    {
        var inconclusiveReport = new StringBuilder();
        inconclusiveReport.AppendLine("\n\n## 🐕 Diffra Self-Evaluation (Dogfood)\n");
        inconclusiveReport.AppendLine("### Assessment: `inconclusive`\n");
        inconclusiveReport.AppendLine("> [!WARNING]");
        inconclusiveReport.Append("> Cannot attribute: no record on `").Append(baselinesBranch).Append("` in the history window of ").Append(HistoryWindowCommits).Append(" first-parent commits ending at merge base `").Append(mergeBaseCommit).Append("`. No comparison was made.\n");
        inconclusiveReport.Append("> Candidate Evidence was generated for commit `").Append(commit).Append("` using pinned judge `").Append(judgeVersion).Append("`.\n");
        inconclusiveReport.AppendLine("> Gate status: **Passed (inconclusive / cannot attribute)**.\n");
        File.AppendAllText(stepSummary, inconclusiveReport.ToString());
    }

    return;
}

Console.WriteLine($"[BASELINE] Resolved baseline Evidence from commit '{matchedCommit}' (merge-base was '{mergeBaseCommit}').");
var comparisonPointNote = DescribeComparisonPoint(mergeBaseCommit, matchedCommit, matchedDistance, ancestorList);
if (comparisonPointNote.Length > 0)
{
    Console.WriteLine("::notice::" + comparisonPointNote.ReplaceLineEndings(" "));
}

// 4. Construct verified Baseline Reference
Console.WriteLine("4. Building verified Baseline Reference document...");
BuildBaselineReference(baselineEvidencePath, matchedCommit, baselineRefPath);

// 5. Diff against baseline (the pinned judge validates the baseline reference ID and structure)
Console.WriteLine("5. Computing typed Delta against merge-base baseline...");
var deltaPath = Path.Combine(workDir, "delta.json");

RunProcess("dotnet", "diffra", "diff",
    "--baseline-ref", baselineRefPath,
    "--baseline-evidence", baselineEvidencePath,
    "--evidence", candidateEvidence,
    "-o", deltaPath);

// 6. Evaluate Delta against quality rules
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
    File.AppendAllText(summaryFile, "\n\n## 🐕 Diffra Self-Evaluation (Dogfood)\n\n" + comparisonPointNote + markdownReport);
    Console.WriteLine($"Appended report summary to $GITHUB_STEP_SUMMARY ({summaryFile}).");
}

if (evalExitCode != 0)
{
    Console.Error.WriteLine($"\n[FAIL] Policy evaluation exited with code {evalExitCode}. Gating failed.");
    Environment.Exit(evalExitCode);
}

Console.WriteLine("\n[SUCCESS] Diffra dogfooding run passed all quality policies!");

// States where the comparison point is when it is not the merge base. Returns an empty string when
// the merge base itself has a record. The commits listed are those between the comparison point and
// the merge base, whose changes the comparison includes.
static string DescribeComparisonPoint(string mergeBase, string comparisonPoint, int distance, string[] firstParentAncestors)
{
    if (distance == 0)
    {
        return "";
    }

    var note = new StringBuilder();
    note.Append("> [!NOTE]\n");
    note.Append("> Compared with `").Append(comparisonPoint[..Math.Min(12, comparisonPoint.Length)])
        .Append("`, ").Append(distance).Append(distance == 1 ? " commit" : " commits")
        .Append(" before the merge base `").Append(mergeBase[..Math.Min(12, mergeBase.Length)])
        .Append("`, because the merge base has no record. Changes from these commits are part of this comparison:\n");
    foreach (var sha in firstParentAncestors.Take(distance))
    {
        note.Append("> - `").Append(sha[..Math.Min(12, sha.Length)]).Append("`\n");
    }

    note.Append('\n');
    return note.ToString();
}

static string ReadJudgeVersion(string repoRoot)
{
    var manifestPath = Path.Combine(repoRoot, ".config", "dotnet-tools.json");
    if (File.Exists(manifestPath))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (doc.RootElement.TryGetProperty("tools", out var tools) &&
            tools.TryGetProperty("diffra.cli", out var cli) &&
            cli.TryGetProperty("version", out var ver) &&
            ver.GetString() is { } versionStr)
        {
            return versionStr;
        }
    }
    return ReadProjectVersion(repoRoot);
}

static string ReadProjectVersion(string repoRoot)
{
    var propsPath = Path.Combine(repoRoot, "Directory.Build.props");
    if (File.Exists(propsPath))
    {
        var text = File.ReadAllText(propsPath);
        var match = System.Text.RegularExpressions.Regex.Match(text, @"<Version>([^<]+)</Version>");
        if (match.Success) return match.Groups[1].Value.Trim();
    }
    throw new InvalidOperationException("Could not determine version from Directory.Build.props.");
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

static string ResolveMergeBaseStrict(string repoRoot)
{
    const string target = "origin/main";
    var verifyExit = RunProcessAllowExitCode("git", "rev-parse", "--verify", target);
    if (verifyExit != 0)
    {
        var fetchExit = RunProcessAllowExitCode("git", "fetch", "origin", "main");
        if (fetchExit != 0)
        {
            Console.Error.WriteLine("[FAIL] Failed to fetch origin/main for merge-base resolution.");
            Environment.Exit(1);
        }
    }

    try
    {
        var mergeBase = CaptureOutput("git", "merge-base", "HEAD", target).Trim();
        if (string.IsNullOrWhiteSpace(mergeBase))
        {
            Console.Error.WriteLine("[FAIL] Null or empty merge-base returned against origin/main.");
            Environment.Exit(1);
        }
        return mergeBase;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[FAIL] Error computing merge-base against origin/main: {ex.Message}");
        Environment.Exit(1);
        throw;
    }
}

static void BuildBaselineReference(string evidencePath, string resolvedCommit, string outputPath)
{
    var evidenceBytes = File.ReadAllBytes(evidencePath);
    using var evidenceDoc = JsonDocument.Parse(evidenceBytes);
    var evidenceId = evidenceDoc.RootElement.GetProperty("id").GetString()!;

    var manifestDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(evidenceBytes));

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
    var refId = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(framed));
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
        throw new InvalidOperationException($"Command '{filename} {string.Join(' ', args)}' failed with exit code {exitCode}.");
    }
}

static int RunProcessAllowExitCode(string filename, params string[] args)
{
    var (exitCode, _, stderr) = RunProcessAllowExitCodeWithOutput(filename, args);
    if (!string.IsNullOrWhiteSpace(stderr) && exitCode != 0)
    {
        Console.Error.WriteLine(stderr);
    }
    return exitCode;
}

static void CheckInvariants(string payloadPath)
{
    Console.WriteLine("Evaluating hard architectural invariants...");
    using var payloadDoc = JsonDocument.Parse(File.ReadAllBytes(payloadPath));
    var metrics = payloadDoc.RootElement.GetProperty("metrics");

    var invariants = new List<(string Name, int Value, int Expected)>
    {
        ("aspireRuntimeDependencies", metrics.GetProperty("aspireRuntimeDependencies").GetInt32(), 0)
    };

    bool anyFailed = false;
    foreach (var (name, value, expected) in invariants)
    {
        if (value == expected)
        {
            Console.WriteLine($"[INVARIANT] {name}={value} PASS");
        }
        else
        {
            Console.Error.WriteLine($"[INVARIANT] {name}={value} FAIL (expected {expected})");
            anyFailed = true;
        }
    }

    if (anyFailed)
    {
        Console.Error.WriteLine("[FAIL] One or more architectural invariants failed!");
        Environment.Exit(1);
    }
}

static (int exitCode, string stdout, string stderr) RunProcessAllowExitCodeWithOutput(string filename, params string[] args)
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

static string CaptureOutput(string filename, params string[] args)
{
    var (exitCode, stdout, stderr) = RunProcessAllowExitCodeWithOutput(filename, args);
    if (exitCode != 0)
    {
        throw new InvalidOperationException($"Command '{filename} {string.Join(' ', args)}' failed with exit code {exitCode}: {stderr}");
    }
    return stdout;
}
