using System.Security.Cryptography;
using Diffra.Regression;

var argsList = args.ToList();
string? mode = null;
string? version = null;
string? tool = null;
string? oldTool = null;
string? newTool = null;
string? oldPkg = null;
string? newPkg = null;
string? corpusRoot = null;
var quick = false;
var strict = false;
var allowPath = false;

for (var i = 0; i < argsList.Count; i++)
{
    var arg = argsList[i];
    switch (arg)
    {
        case "generate" or "--generate":
            mode = "generate";
            if (i + 1 < argsList.Count && !argsList[i + 1].StartsWith('-'))
            {
                version = argsList[++i];
            }
            break;

        case "--compare":
            mode = "compare";
            break;

        case "--test" or "--self-test":
            mode = "test";
            break;

        case "--quick":
            quick = true;
            break;

        case "--strict":
            strict = true;
            break;

        case "--allow-path":
            allowPath = true;
            break;

        case "--tool" or "--candidate":
            if (i + 1 < argsList.Count)
            {
                tool = argsList[++i];
            }
            break;

        case "--old":
            if (i + 1 < argsList.Count)
            {
                oldTool = argsList[++i];
            }
            break;

        case "--new":
            if (i + 1 < argsList.Count)
            {
                newTool = argsList[++i];
            }
            break;

        case "--old-pkg":
            if (i + 1 < argsList.Count)
            {
                oldPkg = argsList[++i];
            }
            break;

        case "--new-pkg":
            if (i + 1 < argsList.Count)
            {
                newPkg = argsList[++i];
            }
            break;

        case "--corpus":
            if (i + 1 < argsList.Count)
            {
                corpusRoot = argsList[++i];
            }
            break;

        case "--help" or "-h":
            PrintUsage();
            return 0;
    }
}

// Find repository root and default corpus root
var currentDir = Directory.GetCurrentDirectory();
var repoRoot = FindRepoRoot(currentDir);
corpusRoot ??= Path.Combine(repoRoot, "tests", "regression");

// Run regression runner internal self-tests
CorpusRunner.RunSelfTests();

if (mode == "test")
{
    Console.WriteLine("All regression runner self-tests passed.");
    return 0;
}

if (mode == "generate")
{
    if (string.IsNullOrWhiteSpace(version))
    {
        Console.Error.WriteLine("Error: 'generate' requires a version string (e.g. '0.0.1-alpha.1').");
        return 2;
    }

    if (string.IsNullOrWhiteSpace(tool))
    {
        Console.Error.WriteLine("Error: 'generate' requires --tool <path-to-diffra>.");
        return 2;
    }

    if (!allowPath && !tool.Contains('/') && !tool.Contains('\\'))
    {
        Console.Error.WriteLine($"Error: Bare tool name '{tool}' is not permitted without --allow-path.");
        return 2;
    }

    var fullTool = Path.GetFullPath(tool);
    if (tool.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
        tool.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
        fullTool.StartsWith(Path.Combine(repoRoot, "src"), StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Error: 'generate' refuses in-tree sources, csproj, or unverified paths. It must use a verified release package or archive executable.");
        return 2;
    }

    Console.WriteLine($"Generating corpus for {version} at {corpusRoot} using {tool}...");
    CorpusGenerator.Generate(corpusRoot, version, tool, repoRoot);
    return 0;
}

var corpora = Directory.EnumerateDirectories(corpusRoot)
    .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
    .OrderBy(Path.GetFileName, StringComparer.Ordinal)
    .ToList();

if (corpora.Count == 0)
{
    Console.Error.WriteLine($"No corpus directories found containing manifest.json under {corpusRoot}");
    return 1;
}

var summary = new RunSummary();

if (mode == "compare")
{
    if (string.IsNullOrWhiteSpace(oldTool) || string.IsNullOrWhiteSpace(newTool))
    {
        Console.Error.WriteLine("Error: '--compare' requires both --old <tool> and --new <tool>.");
        return 2;
    }

    ValidateToolPath(oldTool, "old", allowPath);
    ValidateToolPath(newTool, "new", allowPath);

    Console.WriteLine("============================================================");
    Console.WriteLine("Old vs New Executable Comparison Verification");
    Console.WriteLine("============================================================");
    Console.WriteLine($"Old Tool Path: {oldTool}");
    Console.WriteLine($"New Tool Path: {newTool}");

    var (oldVerCode, oldVerOut, oldVerErr) = ToolExecutor.RunWithOutput(oldTool, ["--version"], allowPath: allowPath);
    var (newVerCode, newVerOut, newVerErr) = ToolExecutor.RunWithOutput(newTool, ["--version"], allowPath: allowPath);
    Console.WriteLine($"Old Tool '--version' output (exit {oldVerCode}): {(!string.IsNullOrWhiteSpace(oldVerOut) ? oldVerOut.Trim() : oldVerErr.Trim())}");
    Console.WriteLine($"New Tool '--version' output (exit {newVerCode}): {(!string.IsNullOrWhiteSpace(newVerOut) ? newVerOut.Trim() : newVerErr.Trim())}");

    var oldVerReported = ToolExecutor.InspectToolVersion(oldTool, allowPath: allowPath);
    var newVerReported = ToolExecutor.InspectToolVersion(newTool, allowPath: allowPath);
    Console.WriteLine($"Old Tool Reported Version: {oldVerReported}");
    Console.WriteLine($"New Tool Reported Version: {newVerReported}");

    if (string.IsNullOrWhiteSpace(oldPkg))
    {
        Console.Error.WriteLine("Error: Missing required --old-pkg <path-to-package> for --compare mode.");
        return 1;
    }
    if (!File.Exists(oldPkg))
    {
        Console.Error.WriteLine($"Error: Old package not found at '{oldPkg}'.");
        return 1;
    }
    var oldPkgSha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(oldPkg)));
    Console.WriteLine($"Old Package:        {oldPkg}");
    Console.WriteLine($"Old Package SHA256: {oldPkgSha}");

    if (string.IsNullOrWhiteSpace(newPkg))
    {
        Console.Error.WriteLine("Error: Missing required --new-pkg <path-to-package> for --compare mode.");
        return 1;
    }
    if (!File.Exists(newPkg))
    {
        Console.Error.WriteLine($"Error: New package not found at '{newPkg}'.");
        return 1;
    }
    var newPkgSha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(newPkg)));
    Console.WriteLine($"New Package:        {newPkg}");
    Console.WriteLine($"New Package SHA256: {newPkgSha}");

    // Strict requirements for --compare: packages and versions must be supplied and distinct (no skips permitted)
    if (string.Equals(oldPkgSha, newPkgSha, StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Error: Old and new package SHA256 digests are identical! Comparison requires distinct package builds.");
        return 1;
    }

    if (oldVerReported == "unknown" || newVerReported == "unknown")
    {
        Console.Error.WriteLine("Error: Tool version cannot be 'unknown' in --compare mode. Both tools must report verifiable distinct versions.");
        return 1;
    }

    if (string.Equals(oldVerReported, newVerReported, StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"Error: Old and new tools reported identical version '{oldVerReported}'! Pinned release and candidate must be distinct builds.");
        return 1;
    }

    Console.WriteLine("Verified: Old and new executables are distinct builds.");
    Console.WriteLine("============================================================");

    if (quick)
    {
        Console.WriteLine("Quick mode: Skipping full old-vs-new comparison execution.");
    }
    else
    {
        foreach (var corpusDir in corpora)
        {
            CorpusRunner.RunComparison(corpusDir, oldTool, newTool, summary, strict: strict, allowPath: allowPath);
            CorpusRunner.RunBackwardReads(corpusDir, newTool, summary, quick: false, allowPath: allowPath);
        }
    }
}
else
{
    // Candidate tool run
    if (string.IsNullOrWhiteSpace(tool))
    {
        Console.Error.WriteLine("Error: Missing required --tool <path-to-diffra>. No in-tree fallback is permitted.");
        return 2;
    }

    ValidateToolPath(tool, "candidate", allowPath);

    foreach (var corpusDir in corpora)
    {
        CorpusRunner.RunCandidate(corpusDir, tool, summary, quick: quick, strict: strict, allowPath: allowPath);
    }
}

// Print overall summary
Console.WriteLine();
Console.WriteLine("============================================================");
Console.WriteLine("Diffra Regression Suite Summary");
Console.WriteLine("============================================================");
Console.WriteLine($"Corpora Evaluated:         {corpora.Count}");
Console.WriteLine($"Total Cases:               {summary.TotalCases}");
Console.WriteLine($"Passed Cases:              {summary.PassedCases}");
Console.WriteLine($"Identity-Unverified Cases: {summary.IdentityUnverifiedCases.Count}");
Console.WriteLine($"Declared Breaks:           {summary.DeclaredBreaks.Count}");
Console.WriteLine($"Failed Cases:              {summary.Failures.Count}");
Console.WriteLine($"Backward Reads:            {(summary.BackwardReadsPassed ? "PASSED" : "FAILED")}");
Console.WriteLine("============================================================");

if (summary.IdentityUnverifiedCases.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Identity-Unverified Cases (matched only after environment/identity normalization):");
    foreach (var (caseId, file) in summary.IdentityUnverifiedCases)
    {
        Console.WriteLine($"  - {caseId} ({file})");
    }
}

if (summary.DeclaredBreaks.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Declared Breaks (skipped):");
    foreach (var (caseId, reason) in summary.DeclaredBreaks)
    {
        Console.WriteLine($"  - {caseId}: {reason}");
    }
}

if (summary.Failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Failures:");
    foreach (var (caseId, file, pointer, message) in summary.Failures)
    {
        var ptr = pointer is not null ? $" [pointer: {pointer}]" : "";
        Console.WriteLine($"  - {caseId} ({file}){ptr}: {message}");
    }
    return 1;
}

if (strict && summary.IdentityUnverifiedCases.Count > 0)
{
    Console.Error.WriteLine($"Strict mode failure: {summary.IdentityUnverifiedCases.Count} case(s) had unverified identity.");
    return 1;
}

if (!summary.BackwardReadsPassed)
{
    Console.Error.WriteLine("Backward reads validation failed.");
    return 1;
}

Console.WriteLine("All regression checks passed.");
return 0;

static void ValidateToolPath(string toolPath, string label, bool allowPath)
{
    if (!allowPath && !toolPath.Contains('/') && !toolPath.Contains('\\'))
    {
        throw new ArgumentException($"Bare {label} tool name '{toolPath}' is not permitted without --allow-path. Specify a relative or absolute file path or pass --allow-path.");
    }

    var fullPath = toolPath.Contains('/') || toolPath.Contains('\\') ? Path.GetFullPath(toolPath) : toolPath;
    if (OperatingSystem.IsWindows() && !File.Exists(fullPath) && File.Exists(fullPath + ".exe"))
    {
        fullPath += ".exe";
    }

    if (!allowPath && !File.Exists(fullPath))
    {
        throw new FileNotFoundException($"{label} tool executable not found at '{fullPath}'.");
    }
}

static void PrintUsage()
{
    Console.WriteLine("Usage: diffra-regression [options]");
    Console.WriteLine("Modes & Options:");
    Console.WriteLine("  --tool <path>                          Run candidate binary against all corpora");
    Console.WriteLine("  --quick                                Quick mode: fast smoke-test skipping slow checks and old-vs-new");
    Console.WriteLine("  --strict                               Strict mode: fail on any IDENTITY-UNVERIFIED cases");
    Console.WriteLine("  --allow-path                           Allow bare tool names to resolve via system PATH");
    Console.WriteLine("  --compare --old <old> --new <new>      Compare two tool builds against all corpora");
    Console.WriteLine("            [--old-pkg <path>]           Path to old package (.nupkg / archive) for SHA-256 check");
    Console.WriteLine("            [--new-pkg <path>]           Path to new package (.nupkg / archive) for SHA-256 check");
    Console.WriteLine("  generate <version> --tool <path>       Generate a new frozen corpus for a release using verified tool");
    Console.WriteLine("  --corpus <path>                        Path to corpus directory (default: tests/regression)");
}

static string FindRepoRoot(string startDir)
{
    var dir = new DirectoryInfo(startDir);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Diffra.slnx")) || Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    return startDir;
}
