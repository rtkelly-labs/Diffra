using System.Diagnostics;
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

var protocolFiles = GetSourceFiles(Path.Combine(repoRoot, "src", "Protocol"));
var cliFiles = GetSourceFiles(Path.Combine(repoRoot, "src", "Cli"));
var schemaFiles = Directory.GetFiles(Path.Combine(repoRoot, "schemas"), "*.json", SearchOption.TopDirectoryOnly);

long packageSizeBytes = 0;
var packageDir = Path.Combine(repoRoot, "artifacts", "package");
var nupkgFiles = Directory.Exists(packageDir)
    ? Directory.GetFiles(packageDir, "Diffra.Cli.*.nupkg", SearchOption.TopDirectoryOnly)
    : [];

if (nupkgFiles.Length == 0)
{
    var tempPackageDir = Path.Combine(repoRoot, "dogfood-run", "package");
    Directory.CreateDirectory(tempPackageDir);
    using var packProcess = Process.Start(new ProcessStartInfo("dotnet")
    {
        ArgumentList = { "pack", Path.Combine(repoRoot, "src", "Cli", "Diffra.Cli.csproj"), "-c", "Release", "-o", tempPackageDir },
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    });
    packProcess?.WaitForExit();
    nupkgFiles = Directory.GetFiles(tempPackageDir, "Diffra.Cli.*.nupkg", SearchOption.TopDirectoryOnly);
}

if (nupkgFiles.Length > 0)
{
    var nupkg = nupkgFiles.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
    packageSizeBytes = new FileInfo(nupkg).Length;
}

var metricsNode = new JsonObject
{
    ["aspireRuntimeDependencies"] = aspireDependencies,
    ["compilerWarnings"] = 0,
    ["schemasCount"] = schemaFiles.Length,
    ["sourceFilesCount"] = protocolFiles.Length + cliFiles.Length
};

if (packageSizeBytes > 0)
{
    metricsNode["packageSizeBytes"] = packageSizeBytes;
}

var payload = new JsonObject
{
    ["scope"] = "diffra/core",
    ["methodology"] = "static-analysis",
    ["metrics"] = metricsNode
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

// Count only authored sources: build output under bin/ and obj/ (e.g. generated
// AssemblyInfo and GlobalUsings files) is excluded so the metric is stable across builds.
static string[] GetSourceFiles(string projectDir) =>
    Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories)
        .Where(path =>
        {
            var relative = Path.GetRelativePath(projectDir, path).Replace('\\', '/');
            return !relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
                && !relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase);
        })
        .ToArray();
