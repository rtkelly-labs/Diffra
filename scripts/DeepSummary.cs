using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

var artifactsDir = args.Length > 0 ? args[0] : "artifacts-summary";
var outputPath = args.Length > 1 ? args[1] : "deep-summary.json";

var errors = new List<string>();

// Environment metadata
var schemaVersion = Environment.GetEnvironmentVariable("SCHEMA_VERSION") ?? "1.0.0";
var commitSha = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? Environment.GetEnvironmentVariable("COMMIT_SHA");
var gitRef = Environment.GetEnvironmentVariable("GITHUB_REF");
var runIdStr = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
long? runId = long.TryParse(runIdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRunId) ? parsedRunId : null;
var runAttemptStr = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT");
int? runAttempt = int.TryParse(runAttemptStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAttempt) ? parsedAttempt : null;
var fuzzBudgetStr = Environment.GetEnvironmentVariable("FUZZ_BUDGET_SECONDS");
int? fuzzBudgetSeconds = int.TryParse(fuzzBudgetStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBudget) ? parsedBudget : null;
var mutationThresholdStr = Environment.GetEnvironmentVariable("MUTATION_THRESHOLD") ?? "57";
int? mutationThreshold = int.TryParse(mutationThresholdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedThreshold) ? parsedThreshold : 57;

// Per-job results from needs object if available
var jobResults = new JsonObject();
var jobResultsJson = Environment.GetEnvironmentVariable("JOB_RESULTS_JSON");
if (!string.IsNullOrEmpty(jobResultsJson))
{
    try
    {
        using var needsDoc = JsonDocument.Parse(jobResultsJson);
        foreach (var job in needsDoc.RootElement.EnumerateObject())
        {
            var result = job.Value.TryGetProperty("result", out var resProp) ? resProp.GetString() : null;
            jobResults[job.Name] = result;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Note: Could not parse JOB_RESULTS_JSON: {ex.Message}");
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
string? unitTestsStatus = null;
string? toolSmokeStatus = null;
double? mutationScore = null;
double? comparisonScore = null;
double? assessmentScore = null;

long? propertyIterations = null;
int? propertiesChecked = null;
int? propertiesPassed = null;
int? propertiesFailed = null;

foreach (var file in Directory.GetFiles(artifactsDir, "*.json", SearchOption.AllDirectories))
{
    var fileName = Path.GetFileName(file);
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
                    var platObj = new JsonObject
                    {
                        ["platform"] = platName,
                        ["runner"] = root.TryGetProperty("runner", out var r) ? r.GetString() : null,
                        ["rid"] = root.TryGetProperty("rid", out var rd) ? rd.GetString() : null,
                        ["archiveSmoke"] = root.TryGetProperty("archiveSmoke", out var smk) ? smk.GetString() : null,
                        ["archiveName"] = root.TryGetProperty("archiveName", out var an) ? an.GetString() : null,
                    };

                    if (root.TryGetProperty("archiveSizeBytes", out var asProp) && asProp.TryGetInt64(out var asVal))
                    {
                        platObj["archiveSizeBytes"] = asVal;
                        archiveSizesBytes[platName] = asVal;
                    }
                    else if (root.TryGetProperty("archiveSize", out var asPropOld) && asPropOld.TryGetInt64(out var asValOld))
                    {
                        platObj["archiveSizeBytes"] = asValOld;
                        archiveSizesBytes[platName] = asValOld;
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

            unitTestsStatus = root.TryGetProperty("unitTests", out var ut) ? ut.GetString() : null;
            toolSmokeStatus = root.TryGetProperty("toolSmoke", out var ts) ? ts.GetString() : null;

            if (root.TryGetProperty("packageSizeBytes", out var psProp) && psProp.TryGetInt64(out var psVal))
            {
                var pkgName = root.TryGetProperty("packageName", out var pn) ? pn.GetString() ?? "Diffra.Cli.nupkg" : "Diffra.Cli.nupkg";
                packageSizesBytes[pkgName] = psVal;
            }
            else if (root.TryGetProperty("packageSize", out var psPropOld) && psPropOld.TryGetInt64(out var psValOld))
            {
                var pkgName = root.TryGetProperty("packageName", out var pn) ? pn.GetString() ?? "Diffra.Cli.nupkg" : "Diffra.Cli.nupkg";
                packageSizesBytes[pkgName] = psValOld;
            }
            else
            {
                errors.Add("test-summary is missing valid packageSizeBytes");
            }
        }
        else if (fileName.StartsWith("properties-summary", StringComparison.OrdinalIgnoreCase))
        {
            if (root.TryGetProperty("iterations", out var itProp) && itProp.TryGetInt64(out var itVal))
            {
                propertyIterations = itVal;
            }
            if (root.TryGetProperty("propertiesChecked", out var pcProp) && pcProp.TryGetInt32(out var pcVal))
            {
                propertiesChecked = pcVal;
            }
            if (root.TryGetProperty("propertiesPassed", out var ppProp) && ppProp.TryGetInt32(out var ppVal))
            {
                propertiesPassed = ppVal;
            }
            if (root.TryGetProperty("propertiesFailed", out var pfProp) && pfProp.TryGetInt32(out var pfVal))
            {
                propertiesFailed = pfVal;
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
                    long? execsPerSecond = null;
                    if (root.TryGetProperty("execsPerSecond", out var eps) && eps.TryGetInt64(out var epsVal))
                    {
                        execsPerSecond = epsVal;
                    }
                    else if (root.TryGetProperty("execPerSecond", out var epsOld) && epsOld.TryGetInt64(out var epsValOld))
                    {
                        execsPerSecond = epsValOld;
                    }

                    int? findings = root.TryGetProperty("findingsCount", out var fc) && fc.TryGetInt32(out var fcVal) ? fcVal : null;
                    var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;

                    if (execs is null) errors.Add($"fuzz target '{targetName}' is missing executions");
                    if (execsPerSecond is null) errors.Add($"fuzz target '{targetName}' is missing execsPerSecond");
                    if (findings is null) errors.Add($"fuzz target '{targetName}' is missing findingsCount");

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

if (!testCount.HasValue) errors.Add("Missing testCount from test-summary");
if (string.IsNullOrEmpty(unitTestsStatus)) errors.Add("Missing unitTestsStatus from test-summary");
if (string.IsNullOrEmpty(toolSmokeStatus)) errors.Add("Missing toolSmokeStatus from test-summary");
if (!mutationScore.HasValue) errors.Add("Missing mutationScore from mutation-report");
if (!comparisonScore.HasValue) errors.Add("Missing comparisonScore from mutation-report");
if (!assessmentScore.HasValue) errors.Add("Missing assessmentScore from mutation-report");
if (!propertyIterations.HasValue) errors.Add("Missing propertyIterations from properties-summary");
if (!propertiesChecked.HasValue) errors.Add("Missing propertiesChecked from properties-summary");
if (!propertiesPassed.HasValue) errors.Add("Missing propertiesPassed from properties-summary");
if (!propertiesFailed.HasValue) errors.Add("Missing propertiesFailed from properties-summary");
if (fuzzTargets.Count == 0) errors.Add("No fuzz targets recorded");

var summary = new JsonObject
{
    ["schemaVersion"] = schemaVersion,
    ["commit"] = commitSha,
    ["ref"] = gitRef,
    ["runId"] = runId,
    ["runAttempt"] = runAttempt,
    ["fuzzBudgetSeconds"] = fuzzBudgetSeconds,
    ["mutationThreshold"] = mutationThreshold,
    ["testCount"] = testCount,
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
    writer.WriteLine($"- **Schema Version:** {schemaVersion}");
    writer.WriteLine($"- **Commit:** {commitSha ?? "n/a"}");
    writer.WriteLine($"- **Test Count:** {testCount?.ToString(CultureInfo.InvariantCulture) ?? "null"}");
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
