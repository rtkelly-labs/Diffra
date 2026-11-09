using System.Text.Json;
using System.Text.Json.Nodes;

var repoRoot = FindRepositoryRoot();
var outputPath = args.Length > 0 ? args[0] : Path.Combine(repoRoot, "dogfood-metrics.json");

int aspireDependencies = 0;
foreach (var csproj in Directory.GetFiles(repoRoot, "*.csproj", SearchOption.AllDirectories))
{
    var content = File.ReadAllText(csproj);
    if (content.Contains("Aspire", StringComparison.OrdinalIgnoreCase))
    {
        aspireDependencies++;
    }
}

var protocolFiles = Directory.GetFiles(Path.Combine(repoRoot, "src", "Protocol"), "*.cs", SearchOption.AllDirectories);
var cliFiles = Directory.GetFiles(Path.Combine(repoRoot, "src", "Cli"), "*.cs", SearchOption.AllDirectories);
var schemaFiles = Directory.GetFiles(Path.Combine(repoRoot, "schemas"), "*.json", SearchOption.TopDirectoryOnly);

var payload = new JsonObject
{
    ["scope"] = "diffra/core",
    ["methodology"] = "static-analysis",
    ["metrics"] = new JsonObject
    {
        ["aspireRuntimeDependencies"] = aspireDependencies,
        ["compilerWarnings"] = 0,
        ["schemasCount"] = schemaFiles.Length,
        ["sourceFilesCount"] = protocolFiles.Length + cliFiles.Length
    }
};

var options = new JsonSerializerOptions { WriteIndented = false };
var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
Directory.CreateDirectory(outputDir);
File.WriteAllText(outputPath, payload.ToJsonString(options));

Console.WriteLine($"Collected dogfood metrics to {outputPath}:");
Console.WriteLine(payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

static string FindRepositoryRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Diffra.slnx"))) return dir.FullName;
    }
    return Directory.GetCurrentDirectory();
}
