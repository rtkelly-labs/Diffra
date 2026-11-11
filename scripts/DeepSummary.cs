using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length < 2)
{
    Console.Error.WriteLine("Error: DeepSummary requires two arguments: <artifacts-dir> <output-json-path>");
    return 1;
}

var artifactsDir = args[0];
var outputPath = args[1];

var errors = new List<string>();

// Environment metadata (strict: no defaulting)
var schemaVersion = Environment.GetEnvironmentVariable("SCHEMA_VERSION");
if (string.IsNullOrEmpty(schemaVersion))
{
    errors.Add("Missing required SCHEMA_VERSION environment variable");
}

var commitSha = Environment.GetEnvironmentVariable("GITHUB_SHA");
if (string.IsNullOrEmpty(commitSha))
{
    errors.Add("Missing required GITHUB_SHA environment variable");
}

var gitRef = Environment.GetEnvironmentVariable("GITHUB_REF");
var runIdStr = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
long? runId = long.TryParse(runIdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRunId) ? parsedRunId : null;
var runAttemptStr = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT");
int? runAttempt = int.TryParse(runAttemptStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAttempt) ? parsedAttempt : null;

var fuzzBudgetStr = Environment.GetEnvironmentVariable("FUZZ_BUDGET_SECONDS");
int? fuzzBudgetSeconds = int.TryParse(fuzzBudgetStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBudget) ? parsedBudget : null;
if (!fuzzBudgetSeconds.HasValue)
{
    errors.Add("Missing or invalid FUZZ_BUDGET_SECONDS environment variable");
}

var strictModeStr = Environment.GetEnvironmentVariable("STRICT");
bool strictMode = string.Equals(strictModeStr, "true", StringComparison.OrdinalIgnoreCase);
if (strictMode)
{
    if (!fuzzBudgetSeconds.HasValue || fuzzBudgetSeconds.Value < 1800)
    {
        errors.Add(string.Create(CultureInfo.InvariantCulture, $"Strict mode requires full fuzz budget (at least 1800s, got {fuzzBudgetSeconds?.ToString(CultureInfo.InvariantCulture) ?? "null"})"));
    }
}

var mutationThresholdStr = Environment.GetEnvironmentVariable("MUTATION_THRESHOLD");
int? mutationThreshold = int.TryParse(mutationThresholdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedThreshold) ? parsedThreshold : null;
if (!mutationThreshold.HasValue)
{
    errors.Add("Missing or invalid MUTATION_THRESHOLD environment variable");
}

// Per-job results from needs object (strict: failure to parse is an error)
var jobResults = new JsonObject();
var jobResultsJson = Environment.GetEnvironmentVariable("JOB_RESULTS_JSON");
if (string.IsNullOrEmpty(jobResultsJson))
{
    errors.Add("Missing required JOB_RESULTS_JSON environment variable");
}
else
{
    try
    {
        using var needsDoc = JsonDocument.Parse(jobResultsJson);
        foreach (var job in needsDoc.RootElement.EnumerateObject())
        {
            var result = job.Value.TryGetProperty("result", out var resProp) ? resProp.GetString() : null;
            jobResults[job.Name] = result;
        }

        string[] expectedJobs = ["platform", "test", "fuzz", "mutation", "properties"];
        foreach (var ej in expectedJobs)
        {
            if (!jobResults.ContainsKey(ej))
            {
                errors.Add($"Missing job result for '{ej}' in JOB_RESULTS_JSON");
            }
            else
            {
                var res = jobResults[ej]?.ToString();
                if (res != "success")
                {
                    errors.Add($"Job '{ej}' result is '{res ?? "null"}' (expected 'success')");
                }
            }
        }
    }
    catch (Exception ex)
    {
        errors.Add($"Failed to parse JOB_RESULTS_JSON: {ex.Message}");
    }
}

if (!Directory.Exists(artifactsDir))
{
    Console.Error.WriteLine($"Error: Artifacts directory '{artifactsDir}' does not exist.");
    return 1;
}

var platforms = new JsonObject();
var archiveSizesBytes = new JsonObject();
var packageSizesBytes = new JsonObject();
var fuzzTargets = new JsonObject();
long? totalFuzzExecutions = 0L;
int? totalFuzzFindings = 0;
int? testCount = null;
int? testsPassed = null;
int? testsFailed = null;
string? unitTestsStatus = null;
string? toolSmokeStatus = null;
double? mutationScore = null;
double? comparisonScore = null;
double? assessmentScore = null;

string? propertiesStatus = null;
long? propertyIterations = null;
int? propertiesChecked = null;
int? propertiesPassed = null;
int? propertiesFailed = null;

var allowedPrefixes = new[] { "platform-", "test-summary", "properties-summary", "fuzz-stats-" };

foreach (var file in Directory.GetFiles(artifactsDir, "*.json", SearchOption.AllDirectories))
{
    var fileName = Path.GetFileName(file);
    if (!allowedPrefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase)) &&
        !fileName.Equals("mutation-report.json", StringComparison.OrdinalIgnoreCase))
    {
        continue;
    }

    try
    {
        var content = File.ReadAllText(file);
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        if (fileName.StartsWith("platform-", StringComparison.OrdinalIgnoreCase))
        {
            if (root.TryGetProperty("platform", out var platProp))
            {
                var platName = platProp.GetString();
                if (!string.IsNullOrEmpty(platName))
                {
                    var archiveSmoke = root.TryGetProperty("archiveSmoke", out var smk) ? smk.GetString() : null;
                    if (archiveSmoke != "success")
                    {
                        errors.Add($"platform '{platName}' archiveSmoke status is '{archiveSmoke ?? "null"}' (expected 'success')");
                    }

                    var platObj = new JsonObject
                    {
                        ["platform"] = platName,
                        ["runner"] = root.TryGetProperty("runner", out var r) ? r.GetString() : null,
                        ["rid"] = root.TryGetProperty("rid", out var rd) ? rd.GetString() : null,
                        ["archiveSmoke"] = archiveSmoke,
                        ["archiveName"] = root.TryGetProperty("archiveName", out var an) ? an.GetString() : null,
                    };

                    if (root.TryGetProperty("archiveSizeBytes", out var asProp) && asProp.TryGetInt64(out var asVal))
                    {
                        platObj["archiveSizeBytes"] = asVal;
                        archiveSizesBytes[platName] = asVal;
                    }
                    else
                    {
                        platObj["archiveSizeBytes"] = null;
                        errors.Add($"platform '{platName}' is missing valid archiveSizeBytes");
                    }

                    platforms[platName] = platObj;
                }
            }
        }
        else if (fileName.StartsWith("test-summary", StringComparison.OrdinalIgnoreCase))
        {
            if (root.TryGetProperty("testCount", out var tcProp) && tcProp.TryGetInt32(out var tc))
            {
                testCount = tc;
            }
            else
            {
                errors.Add("test-summary is missing valid testCount");
            }

            if (root.TryGetProperty("testsPassed", out var tpProp) && tpProp.TryGetInt32(out var tpVal))
            {
                testsPassed = tpVal;
            }
            else
            {
                errors.Add("test-summary is missing valid testsPassed");
            }

            if (root.TryGetProperty("testsFailed", out var tfProp) && tfProp.TryGetInt32(out var tfVal))
            {
                testsFailed = tfVal;
            }
            else
            {
                errors.Add("test-summary is missing valid testsFailed");
            }

            unitTestsStatus = root.TryGetProperty("unitTests", out var ut) ? ut.GetString() : null;
            if (unitTestsStatus != "success")
            {
                errors.Add($"test-summary unitTests status is '{unitTestsStatus ?? "null"}' (expected 'success')");
            }

            toolSmokeStatus = root.TryGetProperty("toolSmoke", out var ts) ? ts.GetString() : null;
            if (toolSmokeStatus != "success")
            {
                errors.Add($"test-summary toolSmoke status is '{toolSmokeStatus ?? "null"}' (expected 'success')");
            }

            if (root.TryGetProperty("packageSizeBytes", out var psProp) && psProp.TryGetInt64(out var psVal))
            {
                if (root.TryGetProperty("packageName", out var pn) && !string.IsNullOrEmpty(pn.GetString()))
                {
                    var pkgName = pn.GetString()!;
                    packageSizesBytes[pkgName] = psVal;
                }
                else
                {
                    errors.Add("test-summary is missing valid packageName");
                }
            }
            else
            {
                errors.Add("test-summary is missing valid packageSizeBytes");
            }
        }
        else if (fileName.StartsWith("properties-summary", StringComparison.OrdinalIgnoreCase))
        {
            propertiesStatus = root.TryGetProperty("status", out var ps) ? ps.GetString() : null;
            if (propertiesStatus != "success")
            {
                errors.Add($"properties-summary status is '{propertiesStatus ?? "null"}' (expected 'success')");
            }

            if (root.TryGetProperty("iterations", out var itProp) && itProp.TryGetInt64(out var itVal))
            {
                propertyIterations = itVal;
            }
            else
            {
                errors.Add("properties-summary is missing valid iterations");
            }

            if (root.TryGetProperty("propertiesChecked", out var pcProp) && pcProp.TryGetInt32(out var pcVal))
            {
                propertiesChecked = pcVal;
            }
            else
            {
                errors.Add("properties-summary is missing valid propertiesChecked");
            }

            if (root.TryGetProperty("propertiesPassed", out var ppProp) && ppProp.TryGetInt32(out var ppVal))
            {
                propertiesPassed = ppVal;
            }
            else
            {
                errors.Add("properties-summary is missing valid propertiesPassed");
            }

            if (root.TryGetProperty("propertiesFailed", out var pfProp) && pfProp.TryGetInt32(out var pfVal))
            {
                propertiesFailed = pfVal;
            }
            else
            {
                errors.Add("properties-summary is missing valid propertiesFailed");
            }
        }
        else if (fileName.StartsWith("fuzz-stats-", StringComparison.OrdinalIgnoreCase))
        {
            if (root.TryGetProperty("target", out var targetProp))
            {
                var targetName = targetProp.GetString();
                if (!string.IsNullOrEmpty(targetName))
                {
                    long? execs = root.TryGetProperty("executions", out var ex) && ex.TryGetInt64(out var exVal) ? exVal : null;
                    long? execsPerSecond = root.TryGetProperty("execsPerSecond", out var eps) && eps.TryGetInt64(out var epsVal) ? epsVal : null;
                    int? findings = root.TryGetProperty("findingsCount", out var fc) && fc.TryGetInt32(out var fcVal) ? fcVal : null;
                    var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;

                    if (execs is null) errors.Add($"fuzz target '{targetName}' is missing executions");
                    if (execsPerSecond is null) errors.Add($"fuzz target '{targetName}' is missing execsPerSecond");
                    if (findings is null) errors.Add($"fuzz target '{targetName}' is missing findingsCount");
                    if (status != "success")
                    {
                        errors.Add($"fuzz target '{targetName}' status is '{status ?? "null"}' (expected 'success')");
                    }

                    fuzzTargets[targetName] = new JsonObject
                    {
                        ["target"] = targetName,
                        ["executions"] = execs,
                        ["execsPerSecond"] = execsPerSecond,
                        ["findingsCount"] = findings,
                        ["status"] = status
                    };

                    if (execs.HasValue) totalFuzzExecutions += execs.Value;
                    if (findings.HasValue) totalFuzzFindings += findings.Value;
                }
            }
        }
        else if (fileName.Equals("mutation-report.json", StringComparison.OrdinalIgnoreCase))
        {
            if (root.TryGetProperty("mutationScore", out var msProp) && msProp.TryGetDouble(out var ms))
            {
                mutationScore = ms;
            }

            if (root.TryGetProperty("files", out var filesProp))
            {
                int totalKilled = 0;
                int totalSurvived = 0;
                int totalTimeout = 0;
                int totalNoCoverage = 0;

                foreach (var fileEntry in filesProp.EnumerateObject())
                {
                    var path = fileEntry.Name;
                    int fKilled = 0, fSurvived = 0, fTimeout = 0, fNoCoverage = 0;

                    if (fileEntry.Value.TryGetProperty("mutants", out var mutantsProp))
                    {
                        foreach (var mutant in mutantsProp.EnumerateArray())
                        {
                            if (mutant.TryGetProperty("status", out var statusProp))
                            {
                                var status = statusProp.GetString();
                                switch (status)
                                {
                                    case "Killed": fKilled++; break;
                                    case "Survived": fSurvived++; break;
                                    case "Timeout": fTimeout++; break;
                                    case "NoCoverage": fNoCoverage++; break;
                                }
                            }
                        }
                    }

                    totalKilled += fKilled;
                    totalSurvived += fSurvived;
                    totalTimeout += fTimeout;
                    totalNoCoverage += fNoCoverage;

                    int fTotal = fKilled + fSurvived + fTimeout + fNoCoverage;
                    if (fTotal > 0)
                    {
                        var fScore = Math.Round((double)(fKilled + fTimeout) / fTotal * 100.0, 2);
                        if (path.EndsWith("Comparison.cs", StringComparison.OrdinalIgnoreCase))
                        {
                            comparisonScore = fScore;
                        }
                        else if (path.EndsWith("Assessment.cs", StringComparison.OrdinalIgnoreCase))
                        {
                            assessmentScore = fScore;
                        }
                    }
                }

                if (!mutationScore.HasValue)
                {
                    int total = totalKilled + totalSurvived + totalTimeout + totalNoCoverage;
                    if (total > 0)
                    {
                        mutationScore = Math.Round((double)(totalKilled + totalTimeout) / total * 100.0, 2);
                    }
                }
            }

            if (!mutationScore.HasValue)
            {
                errors.Add("mutation-report.json missing valid mutationScore");
            }
        }
    }
    catch (Exception ex)
    {
        errors.Add($"Failed to parse {file}: {ex.Message}");
    }
}

// Validate completeness
string[] requiredPlatforms = ["linux-x64", "linux-arm64", "macos-arm64", "macos-x64", "win-x64"];
foreach (var reqPlat in requiredPlatforms)
{
    if (!platforms.ContainsKey(reqPlat))
    {
        errors.Add($"Missing required platform result for '{reqPlat}'");
    }
}

// Validate unit tests: testsFailed must be 0 and testsPassed > 0
if (!testCount.HasValue) errors.Add("Missing testCount from test-summary");
if (!testsPassed.HasValue || testsPassed.Value <= 0)
{
    errors.Add(string.Create(CultureInfo.InvariantCulture, $"testsPassed must be greater than 0 (got {testsPassed?.ToString(CultureInfo.InvariantCulture) ?? "null"})"));
}
if (!testsFailed.HasValue || testsFailed.Value != 0)
{
    errors.Add(string.Create(CultureInfo.InvariantCulture, $"testsFailed must be 0 (got {testsFailed?.ToString(CultureInfo.InvariantCulture) ?? "null"})"));
}
if (string.IsNullOrEmpty(unitTestsStatus)) errors.Add("Missing unitTestsStatus from test-summary");
if (string.IsNullOrEmpty(toolSmokeStatus)) errors.Add("Missing toolSmokeStatus from test-summary");

// Validate mutation scores: must meet threshold and gates
if (!mutationScore.HasValue)
{
    errors.Add("Missing mutationScore from mutation-report");
}
else if (mutationThreshold.HasValue && mutationScore.Value < mutationThreshold.Value)
{
    errors.Add(string.Create(CultureInfo.InvariantCulture, $"mutationScore {mutationScore.Value:F2}% is below threshold {mutationThreshold.Value}%"));
}

if (!comparisonScore.HasValue) errors.Add("Missing comparisonScore from mutation-report");
if (!assessmentScore.HasValue) errors.Add("Missing assessmentScore from mutation-report");

// Validate property checks: propertiesFailed must be 0 and propertiesChecked > 0
if (string.IsNullOrEmpty(propertiesStatus)) errors.Add("Missing propertiesStatus from properties-summary");
if (!propertyIterations.HasValue) errors.Add("Missing propertyIterations from properties-summary");
if (!propertiesChecked.HasValue || propertiesChecked.Value <= 0)
{
    errors.Add(string.Create(CultureInfo.InvariantCulture, $"propertiesChecked must be greater than 0 (got {propertiesChecked?.ToString(CultureInfo.InvariantCulture) ?? "null"})"));
}
if (!propertiesPassed.HasValue || propertiesPassed.Value <= 0)
{
    errors.Add(string.Create(CultureInfo.InvariantCulture, $"propertiesPassed must be greater than 0 (got {propertiesPassed?.ToString(CultureInfo.InvariantCulture) ?? "null"})"));
}
if (!propertiesFailed.HasValue || propertiesFailed.Value != 0)
{
    errors.Add(string.Create(CultureInfo.InvariantCulture, $"propertiesFailed must be 0 (got {propertiesFailed?.ToString(CultureInfo.InvariantCulture) ?? "null"})"));
}

// Validate fuzz targets: require all expected targets (strict mode requires all 8)
var expectedTarget = Environment.GetEnvironmentVariable("FUZZ_TARGET");
string[] allFuzzTargets = ["canonical", "evidence", "document", "delivery-index", "payloads", "line-ranges", "bundle", "bundle-zip"];
string[] requiredTargets = (strictMode || string.IsNullOrEmpty(expectedTarget) || expectedTarget == "all")
    ? allFuzzTargets
    : [expectedTarget];

foreach (var reqTarget in requiredTargets)
{
    if (!fuzzTargets.ContainsKey(reqTarget))
    {
        errors.Add($"Missing required fuzz target result for '{reqTarget}'");
    }
}

var passed = errors.Count == 0;
var summary = new JsonObject
{
    ["status"] = passed ? "pass" : "fail",
    ["result"] = passed ? "success" : "failure",
    ["passed"] = passed,
    ["schemaVersion"] = schemaVersion,
    ["commit"] = commitSha,
    ["ref"] = gitRef,
    ["runId"] = runId,
    ["runAttempt"] = runAttempt,
    ["fuzzBudgetSeconds"] = fuzzBudgetSeconds,
    ["mutationThreshold"] = mutationThreshold,
    ["testCount"] = testCount,
    ["testsPassed"] = testsPassed,
    ["testsFailed"] = testsFailed,
    ["unitTestsStatus"] = unitTestsStatus,
    ["toolSmokeStatus"] = toolSmokeStatus,
    ["mutationScore"] = mutationScore,
    ["comparisonMutationScore"] = comparisonScore,
    ["assessmentMutationScore"] = assessmentScore,
    ["fuzz"] = new JsonObject
    {
        ["totalExecutions"] = totalFuzzExecutions,
        ["findings"] = totalFuzzFindings,
        ["targets"] = fuzzTargets
    },
    ["properties"] = new JsonObject
    {
        ["status"] = propertiesStatus,
        ["iterations"] = propertyIterations,
        ["propertiesChecked"] = propertiesChecked,
        ["propertiesPassed"] = propertiesPassed,
        ["propertiesFailed"] = propertiesFailed
    },
    ["packageSizesBytes"] = packageSizesBytes,
    ["archiveSizesBytes"] = archiveSizesBytes,
    ["platforms"] = platforms,
    ["jobs"] = jobResults
};

var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
var jsonText = summary.ToJsonString(jsonOptions);

var outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
if (!string.IsNullOrEmpty(outDir))
{
    Directory.CreateDirectory(outDir);
}
File.WriteAllText(outputPath, jsonText);

Console.WriteLine($"Wrote deep summary to {outputPath}:");
Console.WriteLine(jsonText);

var stepSummaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
if (!string.IsNullOrEmpty(stepSummaryPath))
{
    using var writer = File.AppendText(stepSummaryPath);
    writer.WriteLine("## Deep Test Tier Summary");
    writer.WriteLine();
    writer.WriteLine($"- **Status:** {(passed ? "PASS" : "FAIL")}");
    writer.WriteLine($"- **Schema Version:** {schemaVersion ?? "null"}");
    writer.WriteLine($"- **Commit:** {commitSha ?? "n/a"}");
    writer.WriteLine($"- **Test Count:** {testCount?.ToString(CultureInfo.InvariantCulture) ?? "null"} ({testsPassed?.ToString(CultureInfo.InvariantCulture) ?? "null"} passed, {testsFailed?.ToString(CultureInfo.InvariantCulture) ?? "null"} failed)");
    writer.WriteLine($"- **Mutation Score (All Protocol):** {(mutationScore.HasValue ? $"{mutationScore.Value:F2}%" : "null")}");
    writer.WriteLine($"- **Comparison.cs Score:** {(comparisonScore.HasValue ? $"{comparisonScore.Value:F2}%" : "null")}");
    writer.WriteLine($"- **Assessment.cs Score:** {(assessmentScore.HasValue ? $"{assessmentScore.Value:F2}%" : "null")}");
    writer.WriteLine($"- **Fuzzing Total Executions:** {totalFuzzExecutions?.ToString("N0", CultureInfo.InvariantCulture) ?? "null"} (Findings: {totalFuzzFindings?.ToString(CultureInfo.InvariantCulture) ?? "null"})");
    writer.WriteLine($"- **Property Checks:** {propertiesPassed?.ToString(CultureInfo.InvariantCulture) ?? "null"}/{propertiesChecked?.ToString(CultureInfo.InvariantCulture) ?? "null"} passed ({propertyIterations?.ToString("N0", CultureInfo.InvariantCulture) ?? "null"} iterations)");
    writer.WriteLine();

    if (platforms.Count > 0)
    {
        writer.WriteLine("### Native Archive Smoke Test Results");
        writer.WriteLine("| Platform | Runner | Archive Smoke | Archive Name | Archive Size |");
        writer.WriteLine("| --- | --- | --- | --- | --- |");
        foreach (var plat in platforms)
        {
            var p = plat.Value!.AsObject();
            var runner = p.TryGetPropertyValue("runner", out var r) ? r?.ToString() : "-";
            var asRes = p.TryGetPropertyValue("archiveSmoke", out var a) ? a?.ToString() : "-";
            var archiveName = p.TryGetPropertyValue("archiveName", out var an) ? an?.ToString() : "-";
            var size = archiveSizesBytes.TryGetPropertyValue(plat.Key, out var s) ? $"{s} bytes" : "-";
            writer.WriteLine($"| {plat.Key} | {runner} | {asRes} | {archiveName} | {size} |");
        }
        writer.WriteLine();
    }

    if (fuzzTargets.Count > 0)
    {
        writer.WriteLine("### Fuzzing Targets");
        writer.WriteLine("| Target | Executions | Execs / s | Findings | Status |");
        writer.WriteLine("| --- | --- | --- | --- | --- |");
        foreach (var target in fuzzTargets)
        {
            var t = target.Value!.AsObject();
            var execs = t.TryGetPropertyValue("executions", out var ex) ? ex?.ToString() : "null";
            var eps = t.TryGetPropertyValue("execsPerSecond", out var ep) ? ep?.ToString() : "null";
            var findings = t.TryGetPropertyValue("findingsCount", out var fn) ? fn?.ToString() : "null";
            var status = t.TryGetPropertyValue("status", out var st) ? st?.ToString() : "-";
            writer.WriteLine($"| {target.Key} | {execs} | {eps} | {findings} | {status} |");
        }
        writer.WriteLine();
    }
}

if (errors.Count > 0)
{
    Console.Error.WriteLine("DeepSummary validation failed with errors:");
    foreach (var err in errors)
    {
        Console.Error.WriteLine($"  - {err}");
    }
    return 1;
}

return 0;
