using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

var repoRoot = FindRepositoryRoot();
var configPath = Path.Combine(repoRoot, ".config", "judge-sha256.json");
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"[FAIL] Missing judge configuration file at: {configPath}");
    Environment.Exit(1);
    return;
}

using var configDoc = JsonDocument.Parse(File.ReadAllText(configPath));
var packageId = configDoc.RootElement.GetProperty("packageId").GetString()!;
var version = configDoc.RootElement.GetProperty("version").GetString()!;
var expectedSha = configDoc.RootElement.GetProperty("sha256").GetString()!.Trim().ToLowerInvariant();

string? toolPath = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--tool-path" && i + 1 < args.Length)
    {
        toolPath = Path.GetFullPath(args[i + 1]);
        i++;
    }
}

var cacheDir = Path.Combine(repoRoot, ".tools-cache");
Directory.CreateDirectory(cacheDir);

var nupkgName = $"{packageId}.{version}.nupkg";
var nupkgPath = Path.Combine(cacheDir, nupkgName);

if (!File.Exists(nupkgPath) || new FileInfo(nupkgPath).Length == 0)
{
    Console.WriteLine($"Downloading {packageId} v{version} from GitHub Releases...");
    var repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "rtkelly-labs/Diffra";
    var ghArgs = new List<string> { "release", "download", $"v{version}", "--repo", repo, "--pattern", nupkgName, "--dir", cacheDir };

    var dlCode = RunProcess("gh", ghArgs.ToArray());
    if (dlCode != 0)
    {
        Console.Error.WriteLine($"[FAIL] Failed to download {nupkgName} from release v{version}.");
        Environment.Exit(dlCode);
        return;
    }
}

// Verify SHA-256
Console.WriteLine($"Verifying SHA-256 checksum for {nupkgName}...");
var fileBytes = File.ReadAllBytes(nupkgPath);
var actualSha = Convert.ToHexStringLower(SHA256.HashData(fileBytes));

if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"[FAIL] Judge package SHA-256 mismatch!\nExpected: {expectedSha}\nActual:   {actualSha}");
    Environment.Exit(1);
    return;
}
Console.WriteLine($"Verified SHA-256 checksum: {actualSha}");

// Ensure no stale package in global packages folder overrides the verified nupkg
var nugetPackagesDir = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
if (string.IsNullOrWhiteSpace(nugetPackagesDir))
{
    nugetPackagesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
}
var globalPkgDir = Path.Combine(nugetPackagesDir, packageId.ToLowerInvariant(), version.ToLowerInvariant());
if (Directory.Exists(globalPkgDir))
{
    Directory.Delete(globalPkgDir, recursive: true);
}

// Create isolated nuget.config
var nugetConfigPath = Path.Combine(cacheDir, "nuget.config");
File.WriteAllText(nugetConfigPath,
    "<configuration><packageSources><clear /><add key=\"local-judge\" value=\".\" /></packageSources></configuration>");

if (!string.IsNullOrEmpty(toolPath))
{
    Directory.CreateDirectory(toolPath);
    Console.WriteLine($"Installing {packageId} tool to {toolPath} via isolated NuGet config...");
    var installCode = RunProcess("dotnet", "tool", "install", packageId, "--tool-path", toolPath, "--version", version, "--configfile", nugetConfigPath);
    if (installCode != 0)
    {
        Console.Error.WriteLine($"[FAIL] dotnet tool install exited with code {installCode}.");
        Environment.Exit(installCode);
        return;
    }
}
else
{
    Console.WriteLine($"Restoring manifest tools via isolated NuGet config...");
    var restoreCode = RunProcess("dotnet", "tool", "restore", "--configfile", nugetConfigPath);
    if (restoreCode != 0)
    {
        Console.Error.WriteLine($"[FAIL] dotnet tool restore exited with code {restoreCode}.");
        Environment.Exit(restoreCode);
        return;
    }
}

Console.WriteLine($"[SUCCESS] Successfully installed verified judge {packageId} {version}.");

static string FindRepositoryRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Diffra.slnx"))) return dir.FullName;
    }
    return Directory.GetCurrentDirectory();
}

static int RunProcess(string filename, params string[] args)
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
    if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout.Trim());
    if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
    {
        Console.Error.WriteLine(stderr.Trim());
    }
    return proc.ExitCode;
}
