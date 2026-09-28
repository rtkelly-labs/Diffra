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
    ["warnAboveAbsoluteIncrease"] = "10",
    ["failAboveAbsoluteIncrease"] = "50"
};
File.WriteAllText(rulesPath, rules.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

// 2. Collect baseline metrics
var payloadPath = Path.Combine(baselinesDir, "baseline-payload.json");
RunProcess("dotnet", "run", Path.Combine(repoRoot, "scripts", "DogfoodMetrics.cs"), "--", payloadPath);

// 3. Collect baseline evidence via diffra CLI
var evidencePath = Path.Combine(baselinesDir, "baseline-evidence.json");
var cliDll = Path.Combine(repoRoot, "src", "Cli", "bin", "Release", "net10.0", "Diffra.Cli.dll");
RunProcess("dotnet", cliDll, "collect",
    "--payload", payloadPath,
    "--subject-id", "diffra/core",
    "--commit", "0000000000000000000000000000000000000000",
    "--type-id", "diffra.metrics.dogfood",
    "--type-version", "1.0",
    "--producer-id", "diffra.dogfood",
    "--producer-version", "0.1.0",
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
        ["resolvedCommit"] = "0000000000000000000000000000000000000000",
        ["sourceRef"] = "main",
        ["version"] = "1.0"
    },
    ["role"] = "merge-base",
    ["schema"] = "https://diffra.dev/schemas/baseline-reference-v1.schema.json",
    ["sourceStore"] = "local",
    ["status"] = "trusted",
    ["subjectCommit"] = "0000000000000000000000000000000000000000",
    ["trust"] = new JsonObject
    {
        ["decisionId"] = "initial-baseline",
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
    var err = proc.StandardError.ReadToEnd();
    proc.WaitForExit();
    if (proc.ExitCode != 0)
    {
        throw new InvalidOperationException($"Command failed ({proc.ExitCode}): {err}");
    }
}
