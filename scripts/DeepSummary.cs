using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

var artifactsDir = args.Length > 0 ? args[0] : "artifacts-summary";
var outputPath = args.Length > 1 ? args[1] : "deep-summary.json";

var platforms = new JsonObject();
var archiveSizes = new JsonObject();
var packageSizes = new JsonObject();
var fuzzTargets = new JsonObject();
long totalFuzzExecutions = 0;
int totalFuzzFindings = 0;
int maxTestCount = 0;
double mutationScore = 0.0;
bool mutationScoreFound = false;

if (Directory.Exists(artifactsDir))
{
    // 1. Process platform & archive results
    foreach (var file in Directory.GetFiles(artifactsDir, "*.json", SearchOption.AllDirectories))
    {
        var fileName = Path.GetFileName(file);
        try
        {
            var content = File.ReadAllText(file);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (fileName.StartsWith("platform-", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("archive-", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("platform", out var platProp))
                {
                    var platName = platProp.GetString() ?? "unknown";
                    if (!platforms.ContainsKey(platName))
                    {
                        platforms[platName] = new JsonObject { ["platform"] = platName };
                    }
                    var platObj = platforms[platName]!.AsObject();

                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.NameEquals("platform")) continue;
                        if (prop.Value.ValueKind == JsonValueKind.Number)
                        {
                            platObj[prop.Name] = prop.Value.GetInt64();
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            platObj[prop.Name] = prop.Value.GetString();
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                        {
                            platObj[prop.Name] = prop.Value.GetBoolean();
                        }
                    }

                    if (root.TryGetProperty("testCount", out var tcProp) && tcProp.TryGetInt32(out var tc) && tc > maxTestCount)
                    {
                        maxTestCount = tc;
                    }
                    if (root.TryGetProperty("archiveSize", out var asProp) && asProp.TryGetInt64(out var asVal))
                    {
                        archiveSizes[platName] = asVal;
                    }
                    if (root.TryGetProperty("packageSize", out var psProp) && psProp.TryGetInt64(out var psVal))
                    {
                        var pkgName = root.TryGetProperty("packageName", out var pn) ? pn.GetString() ?? "Diffra.Cli.nupkg" : "Diffra.Cli.nupkg";
                        packageSizes[pkgName] = psVal;
                    }
                }
            }
            else if (fileName.StartsWith("fuzz-stats-", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("target", out var targetProp))
                {
                    var targetName = targetProp.GetString() ?? "unknown";
                    var execs = root.TryGetProperty("executions", out var ex) && ex.TryGetInt64(out var exVal) ? exVal : 0L;
                    var execPerSec = root.TryGetProperty("execPerSecond", out var eps) && eps.TryGetInt64(out var epsVal) ? epsVal : 0L;
                    var findings = root.TryGetProperty("findingsCount", out var fc) && fc.TryGetInt32(out var fcVal) ? fcVal : 0;
                    var status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "unknown" : "unknown";

                    fuzzTargets[targetName] = new JsonObject
                    {
                        ["executions"] = execs,
                        ["execPerSecond"] = execPerSec,
                        ["findings"] = findings,
                        ["status"] = status
                    };

                    totalFuzzExecutions += execs;
                    totalFuzzFindings += findings;
                }
            }
            else if (fileName.Equals("mutation-report.json", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("mutationScore", out var msProp) && msProp.TryGetDouble(out var ms))
                {
                    mutationScore = ms;
                    mutationScoreFound = true;
                }
                else if (root.TryGetProperty("files", out var filesProp))
                {
                    int killed = 0;
                    int survived = 0;
                    int timeout = 0;
                    int noCoverage = 0;

                    foreach (var fileEntry in filesProp.EnumerateObject())
                    {
                        if (fileEntry.Value.TryGetProperty("mutants", out var mutantsProp))
                        {
                            foreach (var mutant in mutantsProp.EnumerateArray())
                            {
                                if (mutant.TryGetProperty("status", out var statusProp))
                                {
                                    var status = statusProp.GetString();
                                    switch (status)
                                    {
                                        case "Killed": killed++; break;
                                        case "Survived": survived++; break;
                                        case "Timeout": timeout++; break;
                                        case "NoCoverage": noCoverage++; break;
                                    }
                                }
                            }
                        }
                    }

                    int total = killed + survived + timeout + noCoverage;
                    if (total > 0)
                    {
                        mutationScore = Math.Round((double)(killed + timeout) / total * 100.0, 2);
                        mutationScoreFound = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to parse {file}: {ex.Message}");
        }
    }
}
else
{
    Console.WriteLine($"Note: Artifacts directory '{artifactsDir}' not found. Emitting default template summary.");
}

var summary = new JsonObject
{
    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
    ["testCount"] = maxTestCount,
    ["mutationScore"] = mutationScoreFound ? mutationScore : 0.0,
    ["fuzz"] = new JsonObject
    {
        ["totalExecutions"] = totalFuzzExecutions,
        ["findings"] = totalFuzzFindings,
        ["targets"] = fuzzTargets
    },
    ["packageSizes"] = packageSizes,
    ["archiveSizes"] = archiveSizes,
    ["platforms"] = platforms
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
    writer.WriteLine($"- **Test Count:** {maxTestCount}");
    writer.WriteLine($"- **Mutation Score:** {(mutationScoreFound ? $"{mutationScore:F1}%" : "n/a")}");
    writer.WriteLine($"- **Fuzzing Total Executions:** {totalFuzzExecutions:N0} (Findings: {totalFuzzFindings})");
    writer.WriteLine();

    if (platforms.Count > 0)
    {
        writer.WriteLine("### Platform Results");
        writer.WriteLine("| Platform | Runner | Unit Tests | Tool Smoke | Archive Smoke | Archive Size |");
        writer.WriteLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var plat in platforms)
        {
            var p = plat.Value!.AsObject();
            var runner = p.TryGetPropertyValue("runner", out var r) ? r?.ToString() : "-";
            var ut = p.TryGetPropertyValue("unitTests", out var u) ? u?.ToString() : "-";
            var ts = p.TryGetPropertyValue("toolSmoke", out var t) ? t?.ToString() : "-";
            var asRes = p.TryGetPropertyValue("archiveSmoke", out var a) ? a?.ToString() : "-";
            var size = archiveSizes.TryGetPropertyValue(plat.Key, out var s) ? $"{s} bytes" : "-";
            writer.WriteLine($"| {plat.Key} | {runner} | {ut} | {ts} | {asRes} | {size} |");
        }
        writer.WriteLine();
    }

    if (fuzzTargets.Count > 0)
    {
        writer.WriteLine("### Fuzzing Targets");
        writer.WriteLine("| Target | Executions | Exec / s | Findings | Status |");
        writer.WriteLine("| --- | --- | --- | --- | --- |");
        foreach (var target in fuzzTargets)
        {
            var t = target.Value!.AsObject();
            var execs = t.TryGetPropertyValue("executions", out var ex) ? ex?.ToString() : "0";
            var eps = t.TryGetPropertyValue("execPerSecond", out var ep) ? ep?.ToString() : "0";
            var findings = t.TryGetPropertyValue("findings", out var fn) ? fn?.ToString() : "0";
            var status = t.TryGetPropertyValue("status", out var st) ? st?.ToString() : "-";
            writer.WriteLine($"| {target.Key} | {execs} | {eps} | {findings} | {status} |");
        }
        writer.WriteLine();
    }
}
