// Turns deep-summary.json into Diffra Evidence and judges it with the pinned judge.
//
//   dotnet run scripts/DeepEvidence.cs -- payloads <deep-summary.json> <work-dir>
//   dotnet run scripts/DeepEvidence.cs -- evidence <work-dir>
//
// `payloads` writes one structured numeric payload per facet (strength, mutation, throughput, size),
// shaped by baselines/deep/payload.schema.json. `evidence` runs the minimal-change self-check,
// collects Evidence for every facet with the pinned judge from .config/dotnet-tools.json and,
// unless DEEP_EVIDENCE_RECORD is "true", diffs it against the nearest ancestor main deep
// Evidence on the diffra-baselines branch and evaluates it with baselines/deep/<facet>.rules.json.
//
// The pinned evaluator (diffra.numeric-policy 1.0.0) applies one threshold set to every path in
// a Delta and treats only increases as regressions. Every gated metric is therefore written in
// its "weaker is higher" direction, and facets that need different thresholds are separate
// Evidence documents. See docs/artifact-delivery-and-ci.md for the metric encodings.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

const string BaselinesBranch = "diffra-baselines";
const string SubjectId = "diffra/deep";
const string TypeVersion = "1.0";
const string ProducerId = "diffra.deep";
const string ProducerVersion = "1.0.0";
// Every facet methodology starts with this, so a change of encoding is incompatible, not a regression.
const string MethodologyVersion = "deep-v1";
// How far back along first-parent history a baseline is looked for. Deep Evidence is recorded
// nightly, so this covers many days of merges.
const int AncestorSearchDepth = 1000;

string[] facets = ["strength", "mutation", "throughput", "size"];

if (args.Length >= 3 && args[0] == "payloads")
{
    return WritePayloads(args[1], args[2]);
}

if (args.Length >= 2 && args[0] == "evidence")
{
    return RunEvidence(args[1]);
}

Console.Error.WriteLine("Usage: DeepEvidence payloads <deep-summary.json> <work-dir> | evidence <work-dir>");
return 2;

int WritePayloads(string summaryPath, string workDir)
{
    using var summaryDoc = JsonDocument.Parse(File.ReadAllBytes(summaryPath));
    var summary = summaryDoc.RootElement;
    if (!summary.GetProperty("passed").GetBoolean())
    {
        Console.Error.WriteLine("[FAIL] deep-summary.json did not pass; no Evidence is built from a failed run.");
        return 1;
    }

    var strict = summary.GetProperty("strict").GetBoolean();
    var budget = summary.GetProperty("fuzzBudgetSeconds").GetInt32();

    // Strength: any increase is a regression (baselines/deep/strength.rules.json).
    var mutation = summary.GetProperty("mutationCounts");
    var coverage = summary.GetProperty("coverage");
    var strength = new JsonObject
    {
        // The evaluator only flags increases, so the test count is recorded negated: one test
        // fewer is an increase of 1.
        ["tests"] = new JsonObject { ["negatedCount"] = Exact(-summary.GetProperty("testCount").GetInt64(), "tests") },
        ["coverage"] = new JsonObject
        {
            ["uncoveredLineBasisPoints"] = UncoveredBasisPoints(coverage, "linesCovered", "linesValid"),
            ["uncoveredBranchBasisPoints"] = UncoveredBasisPoints(coverage, "branchesCovered", "branchesValid")
        },
        ["fuzz"] = new JsonObject { ["findings"] = Exact(summary.GetProperty("fuzz").GetProperty("findings").GetInt64(), "findings") }
    };

    // Mutation: surviving mutants have a tolerance (baselines/deep/mutation.rules.json), because
    // Stryker timeouts flip between runs of an unchanged tree.
    var mutationMetrics = new JsonObject
    {
        ["protocolSurvivedBasisPoints"] = SurvivedBasisPoints(mutation.GetProperty("protocol")),
        ["comparisonSurvivedBasisPoints"] = SurvivedBasisPoints(mutation.GetProperty("comparison")),
        ["assessmentSurvivedBasisPoints"] = SurvivedBasisPoints(mutation.GetProperty("assessment"))
    };

    // Throughput: time per fuzz execution, so a fall in execs/sec is an increase.
    var throughputTargets = new JsonObject();
    foreach (var target in summary.GetProperty("fuzz").GetProperty("targets").EnumerateObject().OrderBy(t => t.Name, StringComparer.Ordinal))
    {
        var execsPerSecond = target.Value.GetProperty("execsPerSecond").GetInt64();
        if (execsPerSecond <= 0)
        {
            Console.Error.WriteLine($"[FAIL] Fuzz target '{target.Name}' reported {execsPerSecond} execs/sec.");
            return 1;
        }

        throughputTargets[target.Name] = new JsonObject
        {
            ["nanosecondsPerExecution"] = Rounded(1_000_000_000m / execsPerSecond, "ns")
        };
    }

    // Size: archive and package bytes, budgeted by baselines/deep/size.rules.json.
    var archives = new JsonObject();
    foreach (var archive in summary.GetProperty("archiveSizesBytes").EnumerateObject().OrderBy(a => a.Name, StringComparer.Ordinal))
    {
        archives[archive.Name] = Rounded(archive.Value.GetInt64(), "bytes");
    }

    var packages = summary.GetProperty("packageSizesBytes").EnumerateObject().ToArray();
    if (packages.Length != 1)
    {
        Console.Error.WriteLine($"[FAIL] Expected exactly one CLI tool package size, found {packages.Length}.");
        return 1;
    }

    var size = new JsonObject
    {
        ["archives"] = archives,
        ["cliToolPackage"] = Rounded(packages[0].Value.GetInt64(), "bytes")
    };

    // Methodology carries what makes two runs comparable. Throughput depends on the fuzz budget
    // (the corpus grows with time), so runs with a different budget yield an incompatible
    // Delta, which the evaluator reports as inconclusive rather than as a regression.
    var payloads = new Dictionary<string, JsonObject>(StringComparer.Ordinal)
    {
        ["strength"] = Payload("diffra/deep/strength", MethodologyVersion, strength),
        ["mutation"] = Payload("diffra/deep/mutation", MethodologyVersion, new JsonObject { ["mutation"] = mutationMetrics }),
        ["throughput"] = Payload("diffra/deep/throughput", string.Create(CultureInfo.InvariantCulture, $"{MethodologyVersion}; fuzz budget {budget}s per target"), new JsonObject { ["fuzz"] = throughputTargets }),
        ["size"] = Payload("diffra/deep/size", MethodologyVersion, size)
    };

    Directory.CreateDirectory(workDir);
    foreach (var (facet, payload) in payloads)
    {
        AssertJudgeSafe(payload["metrics"]!.AsObject(), string.Empty);
        var path = Path.Combine(workDir, $"payload-{facet}.json");
        File.WriteAllText(path, payload.ToJsonString());
        Console.WriteLine($"Wrote {facet} payload to {path} (strict run: {strict}):");
        Console.WriteLine(payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    return 0;
}

int RunEvidence(string workDir)
{
    var repoRoot = FindRepositoryRoot();
    var commit = RequireEnv("COMMIT_SHA");
    var record = string.Equals(Environment.GetEnvironmentVariable("DEEP_EVIDENCE_RECORD"), "true", StringComparison.Ordinal);
    var stepSummary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
    var report = new StringBuilder();
    report.AppendLine("## Deep test strength (Diffra Evidence)").AppendLine();

    // 1. Self-check: the pinned judge must accept the Delta for the smallest change each metric
    //    can make, so a real change can never be rejected as malformed.
    SelfCheck(workDir, repoRoot, report);

    // 2. Collect Evidence for this run.
    var evidenceDir = Path.Combine(workDir, "evidence");
    Directory.CreateDirectory(evidenceDir);
    foreach (var facet in facets)
    {
        Collect(Path.Combine(workDir, $"payload-{facet}.json"), commit, facet, Path.Combine(evidenceDir, $"{facet}.json"));
    }

    var manifest = new JsonObject
    {
        ["mode"] = record ? "record" : "judge",
        ["commit"] = commit,
        ["facets"] = new JsonArray(facets.Select(f => (JsonNode)f).ToArray())
    };

    if (record)
    {
        manifest["verdict"] = "recorded";
        File.WriteAllText(Path.Combine(workDir, "manifest.json"), manifest.ToJsonString());
        report.AppendLine(CultureInfo.InvariantCulture, $"Recorded Evidence for `{commit}`. The Deep baseline workflow publishes it to `{BaselinesBranch}:evidence/deep/{commit}/` once this run succeeds.");
        WriteOutputs("recorded", report, stepSummary);
        return 0;
    }

    // 3. Resolve the nearest ancestor with main deep Evidence.
    var baselineCommit = ResolveBaselineCommit(commit, out var why);
    if (baselineCommit is null)
    {
        manifest["verdict"] = "inconclusive";
        File.WriteAllText(Path.Combine(workDir, "manifest.json"), manifest.ToJsonString());
        Console.WriteLine($"::notice::Deep Evidence is inconclusive: {why}");
        report.AppendLine("### Verdict: `inconclusive`").AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"> [!NOTE]\n> {why}\n> Evidence for `{commit}` was collected with the pinned judge; nothing was compared, so the gate passes.");
        WriteOutputs("inconclusive", report, stepSummary);
        return 0;
    }

    Console.WriteLine($"[BASELINE] Comparing against main deep Evidence for {baselineCommit}.");
    report.AppendLine(CultureInfo.InvariantCulture, $"Baseline: main deep Evidence for `{baselineCommit}` on `{BaselinesBranch}`.").AppendLine();
    var outcomes = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var facet in facets)
    {
        var facetDir = Path.Combine(workDir, "judge", facet);
        Directory.CreateDirectory(facetDir);
        var baselineEvidence = Path.Combine(facetDir, "baseline-evidence.json");
        var (showExit, showOut, showErr) = Run("git", "show", $"origin/{BaselinesBranch}:evidence/deep/{baselineCommit}/{facet}.json");
        if (showExit != 0)
        {
            if (!IsNotFound(showErr)) throw new InvalidOperationException($"Reading baseline {facet} Evidence failed: {showErr.Trim()}");
            outcomes[facet] = "inconclusive";
            report.AppendLine(CultureInfo.InvariantCulture, $"### {facet}: `inconclusive`").AppendLine().AppendLine(CultureInfo.InvariantCulture, $"No {facet} Evidence at `evidence/deep/{baselineCommit}/{facet}.json`.").AppendLine();
            continue;
        }

        File.WriteAllText(baselineEvidence, showOut);
        var outcome = Judge(facet, facetDir, baselineEvidence, Path.Combine(evidenceDir, $"{facet}.json"), Path.Combine(repoRoot, "baselines", "deep", $"{facet}.rules.json"), baselineCommit, out var markdown);
        outcomes[facet] = outcome;
        report.AppendLine(CultureInfo.InvariantCulture, $"### {facet}: `{outcome}`").AppendLine().AppendLine(markdown).AppendLine();
    }

    var verdict = Combine(outcomes.Values);
    manifest["verdict"] = verdict;
    manifest["baselineCommit"] = baselineCommit;
    manifest["outcomes"] = new JsonObject(outcomes.Select(o => KeyValuePair.Create(o.Key, (JsonNode?)o.Value)));
    File.WriteAllText(Path.Combine(workDir, "manifest.json"), manifest.ToJsonString());
    report.Insert(report.ToString().IndexOf('\n') + 1, $"\n### Verdict: `{verdict}`\n");
    if (verdict == "fail") Console.WriteLine("::error::Deep test strength regressed against the main baseline; see the job summary.");
    WriteOutputs(verdict, report, stepSummary);
    return 0;
}

void SelfCheck(string workDir, string repoRoot, StringBuilder report)
{
    var checkDir = Path.Combine(workDir, "self-check");
    if (Directory.Exists(checkDir)) Directory.Delete(checkDir, recursive: true);
    Directory.CreateDirectory(checkDir);
    const string syntheticCommit = "0000000000000000000000000000000000000001";
    var rows = new List<string>();
    var failures = new List<string>();

    void Expect(string name, string facet, JsonObject baselinePayload, JsonObject candidatePayload, string rulesPath, string expected)
    {
        var dir = Path.Combine(checkDir, name);
        Directory.CreateDirectory(dir);
        var basePayloadPath = Path.Combine(dir, "baseline-payload.json");
        var candPayloadPath = Path.Combine(dir, "candidate-payload.json");
        File.WriteAllText(basePayloadPath, baselinePayload.ToJsonString());
        File.WriteAllText(candPayloadPath, candidatePayload.ToJsonString());
        var baseEvidence = Path.Combine(dir, "baseline-evidence.json");
        var candEvidence = Path.Combine(dir, "candidate-evidence.json");
        Collect(basePayloadPath, syntheticCommit, facet, baseEvidence);
        Collect(candPayloadPath, syntheticCommit, facet, candEvidence);
        var outcome = Judge(facet, dir, baseEvidence, candEvidence, rulesPath, syntheticCommit, out _);
        rows.Add($"| {name} | `{expected}` | `{outcome}` |");
        if (outcome != expected) failures.Add($"{name}: expected '{expected}', got '{outcome}'");
    }

    foreach (var facet in facets)
    {
        var payload = JsonNode.Parse(File.ReadAllText(Path.Combine(workDir, $"payload-{facet}.json")))!.AsObject();
        var rules = Path.Combine(repoRoot, "baselines", "deep", $"{facet}.rules.json");
        // The baseline is this run's real payload; the candidate moves every metric by the
        // smallest step its encoding allows. The Delta then holds the smallest relative change
        // each metric can produce, which is what the pinned judge's decimal check must accept.
        Expect($"{facet}-min-up", facet, payload, Perturb(payload, up: true), rules, facet == "strength" ? "fail" : "pass");
        Expect($"{facet}-min-down", facet, payload, Perturb(payload, up: false), rules, "pass");
    }

    // A clear regression per facet must fail.
    var strengthPayload = JsonNode.Parse(File.ReadAllText(Path.Combine(workDir, "payload-strength.json")))!.AsObject();
    var weaker = strengthPayload.DeepClone().AsObject();
    var testsNode = weaker["metrics"]!["tests"]!["negatedCount"]!;
    testsNode["value"] = testsNode["value"]!.GetValue<decimal>() + 1;
    Expect("strength-one-test-fewer", "strength", strengthPayload, weaker, Path.Combine(repoRoot, "baselines", "deep", "strength.rules.json"), "fail");
    // Mutation tolerates timeout flips (about 10 bp seen on an unchanged tree), warns above
    // 25 bp and fails above 100 bp.
    var mutationPayload = JsonNode.Parse(File.ReadAllText(Path.Combine(workDir, "payload-mutation.json")))!.AsObject();
    var mutationRules = Path.Combine(repoRoot, "baselines", "deep", "mutation.rules.json");
    Expect("mutation-noise-10bp", "mutation", mutationPayload, Shift(mutationPayload, 10), mutationRules, "pass");
    Expect("mutation-weaker-26bp", "mutation", mutationPayload, Shift(mutationPayload, 26), mutationRules, "warn");
    Expect("mutation-weaker-101bp", "mutation", mutationPayload, Shift(mutationPayload, 101), mutationRules, "fail");
    // Throughput is warn-only until nightly data supports a fail threshold.
    var throughputPayload = JsonNode.Parse(File.ReadAllText(Path.Combine(workDir, "payload-throughput.json")))!.AsObject();
    Expect("throughput-half-speed", "throughput", throughputPayload, Scale(throughputPayload, 2.1m), Path.Combine(repoRoot, "baselines", "deep", "throughput.rules.json"), "warn");
    var sizePayload = JsonNode.Parse(File.ReadAllText(Path.Combine(workDir, "payload-size.json")))!.AsObject();
    Expect("size-grows-a-quarter", "size", sizePayload, Scale(sizePayload, 1.3m), Path.Combine(repoRoot, "baselines", "deep", "size.rules.json"), "fail");
    Expect("size-grows-a-tenth", "size", sizePayload, Scale(sizePayload, 1.1m), Path.Combine(repoRoot, "baselines", "deep", "size.rules.json"), "warn");

    // The encoding bounds themselves: the largest exact value and the coarsest rounded step.
    var bounds = Path.Combine(checkDir, "bounds.rules.json");
    File.WriteAllText(bounds, """{"id":"diffra.deep.self-check-bounds","version":"1.0.0","warnAboveRelativeIncrease":"1"}""");
    Expect("encoding-bounds", "strength",
        Payload("diffra/deep/bounds", MethodologyVersion, new JsonObject { ["exact"] = 10000, ["exactNegated"] = -10000, ["rounded"] = 100000, ["roundedUp"] = 999000 }),
        Payload("diffra/deep/bounds", MethodologyVersion, new JsonObject { ["exact"] = 10001, ["exactNegated"] = -9999, ["rounded"] = 99900, ["roundedUp"] = 1000000 }),
        bounds, "pass");

    // Control: a relative change of 1e-5, which these encodings never produce. While the pinned
    // judge rejects it, the bounds above are needed; once it accepts it (the judge pin has moved
    // past the decimal-formatting fix), the bounds and AssertJudgeSafe can be removed.
    var controlDir = Path.Combine(checkDir, "decimal-control");
    Directory.CreateDirectory(controlDir);
    var controlBase = Path.Combine(controlDir, "baseline-payload.json");
    var controlCand = Path.Combine(controlDir, "candidate-payload.json");
    File.WriteAllText(controlBase, Payload("diffra/deep/bounds", MethodologyVersion, new JsonObject { ["value"] = 100000 }).ToJsonString());
    File.WriteAllText(controlCand, Payload("diffra/deep/bounds", MethodologyVersion, new JsonObject { ["value"] = 100001 }).ToJsonString());
    Collect(controlBase, syntheticCommit, "strength", Path.Combine(controlDir, "baseline-evidence.json"));
    Collect(controlCand, syntheticCommit, "strength", Path.Combine(controlDir, "candidate-evidence.json"));
    string control;
    try
    {
        control = "accepted: " + Judge("control", controlDir, Path.Combine(controlDir, "baseline-evidence.json"), Path.Combine(controlDir, "candidate-evidence.json"), bounds, syntheticCommit, out _);
        Console.WriteLine("::notice::The pinned judge now accepts a 1e-5 relative change; the deep metric bounds can be relaxed.");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("(exit 2)", StringComparison.Ordinal))
    {
        control = "rejected (bounds still required)";
    }

    rows.Add($"| decimal-control (1e-5, informational) | `rejected` | `{control}` |");

    report.AppendLine("<details><summary>Pinned-judge self-check (synthetic baselines from this run)</summary>").AppendLine();
    report.AppendLine("| Case | Expected | Outcome |").AppendLine("| --- | --- | --- |");
    foreach (var row in rows) report.AppendLine(row);
    report.AppendLine().AppendLine("</details>").AppendLine();

    if (failures.Count > 0)
    {
        throw new InvalidOperationException("Pinned-judge self-check failed:\n  " + string.Join("\n  ", failures));
    }

    Console.WriteLine($"[SELF-CHECK] {rows.Count} synthetic comparisons gave the expected outcomes.");
}

// Runs diff and eval with the pinned judge and returns the assessment outcome. A judge error
// (exit 1 or 2, which is how a malformed decimal surfaces) throws, so it can never pass as a verdict.
string Judge(string facet, string dir, string baselineEvidence, string candidateEvidence, string rulesPath, string baselineCommit, out string markdown)
{
    var baselineRef = Path.Combine(dir, "baseline-reference.json");
    var delta = Path.Combine(dir, "delta.json");
    var assessment = Path.Combine(dir, "assessment.json");
    BuildBaselineReference(baselineEvidence, baselineCommit, baselineRef);
    RunChecked("dotnet", "diffra", "diff", "--baseline-ref", baselineRef, "--baseline-evidence", baselineEvidence, "--evidence", candidateEvidence, "-o", delta);
    var (evalExit, _, evalErr) = Run("dotnet", "diffra", "eval", "--rules", rulesPath, "--delta", delta, "-o", assessment);
    if (evalExit is not (0 or 3))
    {
        throw new InvalidOperationException($"Pinned judge eval rejected the {facet} Delta (exit {evalExit}): {evalErr.Trim()}");
    }

    using var doc = JsonDocument.Parse(File.ReadAllBytes(assessment));
    var outcome = doc.RootElement.GetProperty("outcome").GetString()!;
    var (reportExit, reportOut, reportErr) = Run("dotnet", "diffra", "report", delta, "--assessment", assessment, "--baseline-ref", baselineRef, "--format", "markdown");
    markdown = reportExit == 0 ? reportOut.Trim() : $"(report failed: {reportErr.Trim()})";
    return outcome;
}

void Collect(string payloadPath, string commit, string facet, string output) =>
    RunChecked("dotnet", "diffra", "collect",
        "--payload", payloadPath,
        "--subject-id", SubjectId,
        "--commit", commit,
        "--type-id", $"diffra.metrics.deep-{facet}",
        "--type-version", TypeVersion,
        "--producer-id", ProducerId,
        "--producer-version", ProducerVersion,
        "-o", output);

string? ResolveBaselineCommit(string commit, out string why)
{
    var (lsExit, lsOut, lsErr) = Run("git", "ls-remote", "--heads", "origin", BaselinesBranch);
    if (lsExit != 0) throw new InvalidOperationException($"git ls-remote failed: {lsErr.Trim()}");
    if (string.IsNullOrWhiteSpace(lsOut))
    {
        why = $"The `{BaselinesBranch}` branch does not exist yet (bootstrap).";
        return null;
    }

    RunChecked("git", "fetch", "--no-tags", "origin", $"+refs/heads/{BaselinesBranch}:refs/remotes/origin/{BaselinesBranch}");
    var (treeExit, treeOut, treeErr) = Run("git", "ls-tree", "-d", "--name-only", $"origin/{BaselinesBranch}:evidence/deep");
    if (treeExit != 0)
    {
        if (!IsNotFound(treeErr)) throw new InvalidOperationException($"Listing deep Evidence failed: {treeErr.Trim()}");
        why = $"No main deep Evidence has been recorded on `{BaselinesBranch}` yet.";
        return null;
    }

    var recorded = treeOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
    var ancestors = Capture("git", "rev-list", "--first-parent", "-n", AncestorSearchDepth.ToString(CultureInfo.InvariantCulture), commit)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    // The run's own commit is skipped: a commit is never compared with itself.
    var match = ancestors.FirstOrDefault(sha => sha != commit && recorded.Contains(sha));
    why = match is null
        ? $"No main deep Evidence exists for any first-parent ancestor of `{commit}` within {AncestorSearchDepth} commits."
        : string.Empty;
    return match;
}

void BuildBaselineReference(string evidencePath, string resolvedCommit, string outputPath)
{
    var evidenceBytes = File.ReadAllBytes(evidencePath);
    using var evidenceDoc = JsonDocument.Parse(evidenceBytes);
    var evidenceId = evidenceDoc.RootElement.GetProperty("id").GetString()!;
    var subjectCommit = evidenceDoc.RootElement.GetProperty("subject").GetProperty("commit").GetString()!;
    if (subjectCommit != resolvedCommit)
    {
        throw new InvalidOperationException($"Baseline Evidence is for '{subjectCommit}', expected '{resolvedCommit}'.");
    }

    var baselineRef = new JsonObject
    {
        ["evidenceId"] = evidenceId,
        ["manifestDigest"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(evidenceBytes)),
        ["resolver"] = new JsonObject
        {
            ["id"] = "diffra.deep.resolver",
            ["resolvedCommit"] = resolvedCommit,
            ["sourceRef"] = "main",
            ["version"] = "1.0"
        },
        ["role"] = "approved-main",
        ["schema"] = "https://diffra.net/schemas/baseline-reference-v1.schema.json",
        ["sourceStore"] = BaselinesBranch,
        ["status"] = "trusted",
        ["subjectCommit"] = resolvedCommit,
        ["trust"] = new JsonObject
        {
            ["decisionId"] = $"deep-main:{resolvedCommit[..Math.Min(12, resolvedCommit.Length)]}",
            ["issuer"] = "trusted-ci"
        }
    };

    var framed = Encoding.ASCII.GetBytes("diffra/v1\0baseline-reference\0").Concat(Encoding.UTF8.GetBytes(SortJson(baselineRef))).ToArray();
    baselineRef["id"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(framed));
    File.WriteAllText(outputPath, SortJson(baselineRef));
}

static string Combine(IEnumerable<string> outcomes)
{
    var all = outcomes.ToArray();
    foreach (var level in new[] { "fail", "inconclusive", "needs-review", "warn" })
    {
        if (all.Contains(level)) return level;
    }

    return "pass";
}

static void WriteOutputs(string verdict, StringBuilder report, string? stepSummary)
{
    Console.WriteLine(report.ToString());
    if (!string.IsNullOrEmpty(stepSummary)) File.AppendAllText(stepSummary, report.ToString());
    var output = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
    if (!string.IsNullOrEmpty(output)) File.AppendAllText(output, $"verdict={verdict}\n");
    Console.WriteLine($"Deep Evidence verdict: {verdict}");
}

static JsonObject Payload(string scope, string methodology, JsonObject metrics) => new()
{
    ["scope"] = scope,
    ["methodology"] = methodology,
    ["metrics"] = metrics
};

static JsonObject Exact(long value, string unit) => new() { ["value"] = value, ["unit"] = unit };

// Exact integer metrics stay within +/-10000, so any change of 1 is a relative change of at
// least 1e-4.
static JsonObject SurvivedBasisPoints(JsonElement counts)
{
    var killed = counts.GetProperty("killed").GetInt64();
    var timeout = counts.GetProperty("timeout").GetInt64();
    var survived = counts.GetProperty("survived").GetInt64();
    var noCoverage = counts.GetProperty("noCoverage").GetInt64();
    var total = killed + timeout + survived + noCoverage;
    if (total <= 0) throw new InvalidOperationException("Mutation report has no scored mutants.");
    return Exact(BasisPoints(survived + noCoverage, total), "basis-points");
}

static JsonObject UncoveredBasisPoints(JsonElement coverage, string coveredName, string validName)
{
    var covered = coverage.GetProperty(coveredName).GetInt64();
    var valid = coverage.GetProperty(validName).GetInt64();
    if (valid <= 0 || covered < 0 || covered > valid) throw new InvalidOperationException($"Invalid coverage counts {coveredName}={covered}, {validName}={valid}.");
    return Exact(BasisPoints(valid - covered, valid), "basis-points");
}

static long BasisPoints(long part, long total) => (long)Math.Round(part * 10000m / total, MidpointRounding.AwayFromZero);

// Rounded metrics keep three significant figures, so the smallest step between two values is
// about 1e-3 of the value: well clear of the 1e-4 limit, whatever the magnitude.
static JsonObject Rounded(decimal value, string unit) => new() { ["value"] = RoundSignificant(value), ["unit"] = unit };

static long RoundSignificant(decimal value)
{
    if (value < 100) throw new InvalidOperationException($"Rounded metric {value} is below 100; three significant figures would not be an integer.");
    var quantum = StepBelowMagnitude(value);
    return (long)(Math.Round(value / quantum, MidpointRounding.AwayFromZero) * quantum);
}

// One unit in the third significant figure of `value`.
static decimal StepBelowMagnitude(decimal value)
{
    decimal quantum = 1;
    while (value >= quantum * 1000) quantum *= 10;
    return quantum;
}

// Asserts the invariant the pinned judge needs: every metric is either an exact integer within
// +/-10000 (unit "tests", "findings" or "basis-points") or an integer of at least 100 with at
// most three significant figures (unit "ns" or "bytes").
static void AssertJudgeSafe(JsonObject node, string path)
{
    foreach (var (name, child) in node)
    {
        var childPath = $"{path}/{name}";
        var obj = child!.AsObject();
        if (!obj.ContainsKey("value"))
        {
            AssertJudgeSafe(obj, childPath);
            continue;
        }

        var value = obj["value"]!.GetValue<long>();
        var unit = obj["unit"]!.GetValue<string>();
        var ok = unit is "ns" or "bytes"
            ? value >= 100 && RoundSignificant(value) == value
            : Math.Abs(value) <= 10000;
        if (!ok)
        {
            throw new InvalidOperationException($"Metric '{childPath}' = {value} {unit} breaks the pinned judge's decimal bound; see docs/artifact-delivery-and-ci.md.");
        }
    }
}

// Moves every metric by its smallest representable step. Zero exact metrics are left at zero
// when moving down, since counts cannot be negative.
static JsonObject Perturb(JsonObject payload, bool up)
{
    var copy = payload.DeepClone().AsObject();
    Visit(copy["metrics"]!.AsObject());
    return copy;

    void Visit(JsonObject node)
    {
        foreach (var (_, child) in node.ToArray())
        {
            var obj = child!.AsObject();
            if (!obj.ContainsKey("value"))
            {
                Visit(obj);
                continue;
            }

            var value = obj["value"]!.GetValue<long>();
            var unit = obj["unit"]!.GetValue<string>();
            long next;
            if (unit is "ns" or "bytes")
            {
                // Moving down across a power of ten lands on the finer grid below it.
                next = up ? value + (long)StepBelowMagnitude(value) : value - (long)StepBelowMagnitude(value - 1);
            }
            else
            {
                next = up ? value + 1 : (value == 0 ? 0 : value - 1);
            }

            obj["value"] = next;
        }
    }
}

static JsonObject Scale(JsonObject payload, decimal factor)
{
    var copy = payload.DeepClone().AsObject();
    Visit(copy["metrics"]!.AsObject());
    return copy;

    void Visit(JsonObject node)
    {
        foreach (var (_, child) in node.ToArray())
        {
            var obj = child!.AsObject();
            if (!obj.ContainsKey("value")) { Visit(obj); continue; }
            obj["value"] = RoundSignificant(obj["value"]!.GetValue<long>() * factor);
        }
    }
}

static JsonObject Shift(JsonObject payload, long delta)
{
    var copy = payload.DeepClone().AsObject();
    Visit(copy["metrics"]!.AsObject());
    return copy;

    void Visit(JsonObject node)
    {
        foreach (var (_, child) in node.ToArray())
        {
            var obj = child!.AsObject();
            if (!obj.ContainsKey("value")) { Visit(obj); continue; }
            obj["value"] = Math.Min(10000, obj["value"]!.GetValue<long>() + delta);
        }
    }
}

static bool IsNotFound(string stderr) =>
    stderr.Contains("does not exist in", StringComparison.OrdinalIgnoreCase) ||
    stderr.Contains("exists on disk, but not in", StringComparison.OrdinalIgnoreCase) ||
    stderr.Contains("Not a valid object name", StringComparison.OrdinalIgnoreCase);

static string SortJson(JsonObject obj)
{
    var res = new JsonObject();
    foreach (var (k, v) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
    {
        res[k] = v is JsonObject child ? JsonNode.Parse(SortJson(child)) : v?.DeepClone();
    }

    return res.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}

static string RequireEnv(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value
    : throw new InvalidOperationException($"Missing required {name} environment variable.");

static string FindRepositoryRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Diffra.slnx"))) return dir.FullName;
    }

    return Directory.GetCurrentDirectory();
}

static void RunChecked(string filename, params string[] args)
{
    var (exit, _, stderr) = Run(filename, args);
    if (exit != 0) throw new InvalidOperationException($"'{filename} {string.Join(' ', args)}' failed with exit code {exit}: {stderr.Trim()}");
}

static string Capture(string filename, params string[] args)
{
    var (exit, stdout, stderr) = Run(filename, args);
    if (exit != 0) throw new InvalidOperationException($"'{filename} {string.Join(' ', args)}' failed with exit code {exit}: {stderr.Trim()}");
    return stdout;
}

static (int Exit, string Stdout, string Stderr) Run(string filename, params string[] args)
{
    var start = new ProcessStartInfo(filename) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var arg in args) start.ArgumentList.Add(arg);
    using var proc = Process.Start(start)!;
    var stdoutTask = proc.StandardOutput.ReadToEndAsync();
    var stderrTask = proc.StandardError.ReadToEndAsync();
    proc.WaitForExit();
    return (proc.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
}
