using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var repoRoot = FindRepositoryRoot();
var baselinesDir = Path.Combine(repoRoot, "baselines", "dogfood");
Directory.CreateDirectory(baselinesDir);

// 1. Write rules.json
var rulesPath = Path.Combine(baselinesDir, "rules.json");
var rules = new JsonObject
{
    ["id"] = "diffra.dogfood.quality-budget",
    ["version"] = "1.0.0",
    ["warnAboveAbsoluteIncrease"] = "50000",
    ["failAboveAbsoluteIncrease"] = "200000",
    ["warnAboveRelativeIncrease"] = "0.10",
    ["failAboveRelativeIncrease"] = "0.50"
};
File.WriteAllText(rulesPath, rules.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

// 2. Collect baseline metrics
var payloadPath = Path.Combine(baselinesDir, "baseline-payload.json");
RunProcess("dotnet", "run", Path.Combine(repoRoot, "scripts", "DogfoodMetrics.cs"), "--", payloadPath);

// 3. Collect baseline evidence via diffra CLI
var evidencePath = Path.Combine(baselinesDir, "baseline-evidence.json");
const string baselineCommit = "da460fca7fefb8e51a501cbe598d77ec7c4445ff";
RunProcess("dotnet", "diffra", "collect",
    "--payload", payloadPath,
    "--subject-id", "diffra/core",
    "--commit", baselineCommit,
    "--type-id", "diffra.metrics.dogfood",
    "--type-version", "1.0",
    "--producer-id", "diffra.dogfood",
    "--producer-version", "0.0.1-alpha.1",
    "-o", evidencePath);

// 4. Generate baseline-reference.json
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
        ["resolvedCommit"] = baselineCommit,
        ["sourceRef"] = "main",
        ["version"] = "1.0"
    },
    ["role"] = "merge-base",
    ["schema"] = "https://diffra.dev/schemas/baseline-reference-v1.schema.json",
    ["sourceStore"] = "diffra-baselines",
    ["status"] = "trusted",
    ["subjectCommit"] = baselineCommit,
    ["trust"] = new JsonObject
    {
        ["decisionId"] = $"merge-base:{baselineCommit[..12]}",
        ["issuer"] = "trusted-ci"
    }
};

// Sort keys for canonical JSON
var sortedJson = SortJson(baselineRef);
var unsignedBytes = Encoding.UTF8.GetBytes(sortedJson);
var framed = Encoding.ASCII.GetBytes("diffra/v1\0baseline-reference\0").Concat(unsignedBytes).ToArray();
var refId = "sha256:" + Convert.ToHexString(SHA256.HashData(framed)).ToLowerInvariant();
baselineRef["id"] = refId;

var finalJson = SortJson(baselineRef);
var refPath = Path.Combine(baselinesDir, "baseline-reference.json");
File.WriteAllText(refPath, finalJson);

Console.WriteLine($"Initialized dogfood baseline at {baselinesDir}:");
Console.WriteLine($"  Evidence ID: {evidenceId}");
Console.WriteLine($"  Reference ID: {refId}");

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
    var start = new ProcessStartInfo(filename)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    foreach (var arg in args) start.ArgumentList.Add(arg);
    using var proc = Process.Start(start)!;
    // Drain both pipes concurrently so a child filling either buffer cannot deadlock.
    var stdoutTask = proc.StandardOutput.ReadToEndAsync();
    var stderrTask = proc.StandardError.ReadToEndAsync();
    proc.WaitForExit();
    _ = stdoutTask.GetAwaiter().GetResult();
    var err = stderrTask.GetAwaiter().GetResult();
    if (proc.ExitCode != 0)
    {
        throw new InvalidOperationException($"Command failed ({proc.ExitCode}): {err}");
    }
}
