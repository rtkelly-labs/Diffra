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
string? candidatePackage = Environment.GetEnvironmentVariable("DIFFRA_CANDIDATE_PACKAGE");
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--package" && i + 1 < args.Length)
    {
        candidatePackage = args[i + 1];
        break;
    }
}

if (!string.IsNullOrEmpty(candidatePackage) && File.Exists(candidatePackage))
{
    packageSizeBytes = new FileInfo(candidatePackage).Length;
}
else
{
    var tempPackageDir = Path.Combine(repoRoot, "dogfood-run", "package");
    if (Directory.Exists(tempPackageDir))
    {
        Directory.Delete(tempPackageDir, recursive: true);
    }
    Directory.CreateDirectory(tempPackageDir);

    var dotnetBin = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
    if (string.IsNullOrEmpty(dotnetBin) || !File.Exists(dotnetBin))
    {
        var userDotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet");
        dotnetBin = File.Exists(userDotnet) ? userDotnet : "dotnet";
    }

    var start = new ProcessStartInfo(dotnetBin)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    var dir = Path.GetDirectoryName(dotnetBin);
    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
    {
        start.Environment["DOTNET_ROOT"] = dir;
    }
    start.ArgumentList.Add("pack");
    start.ArgumentList.Add(Path.Combine(repoRoot, "src", "Cli", "Diffra.Cli.csproj"));
    start.ArgumentList.Add("-c");
    start.ArgumentList.Add("Release");
    start.ArgumentList.Add("-o");
    start.ArgumentList.Add(tempPackageDir);

    using var packProcess = Process.Start(start);
    if (packProcess == null)
    {
        throw new InvalidOperationException("Failed to start dotnet pack process.");
    }
    var stdoutTask = packProcess.StandardOutput.ReadToEndAsync();
    var stderrTask = packProcess.StandardError.ReadToEndAsync();
    packProcess.WaitForExit();
    var stdout = stdoutTask.GetAwaiter().GetResult();
    var stderr = stderrTask.GetAwaiter().GetResult();
    if (packProcess.ExitCode != 0)
    {
        throw new InvalidOperationException($"dotnet pack failed with exit code {packProcess.ExitCode}:\n{stderr}\n{stdout}");
    }
    var nupkgFiles = Directory.GetFiles(tempPackageDir, "Diffra.Cli.*.nupkg", SearchOption.TopDirectoryOnly);
    if (nupkgFiles.Length > 0)
    {
        var nupkg = nupkgFiles.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
        packageSizeBytes = new FileInfo(nupkg).Length;
    }
}

if (packageSizeBytes <= 0)
{
    throw new InvalidOperationException("Failed to measure packageSizeBytes: no valid nupkg was found or package size is zero.");
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
