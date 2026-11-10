using Diffra.Regression;

var argsList = args.ToList();
string? mode = null;
string? version = null;
string? tool = null;
string? oldTool = null;
string? newTool = null;
string? corpusRoot = null;

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

    Console.WriteLine($"Generating corpus for {version} at {corpusRoot} using {tool}...");
    CorpusGenerator.Generate(corpusRoot, version, tool);
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

    foreach (var corpusDir in corpora)
    {
        CorpusRunner.RunComparison(corpusDir, oldTool, newTool, summary);
        CorpusRunner.RunBackwardReads(corpusDir, summary);
    }
}
else
{
    // Candidate tool run
    tool ??= Path.Combine(repoRoot, "src", "Cli", "Diffra.Cli.csproj");

    foreach (var corpusDir in corpora)
    {
        CorpusRunner.RunCandidate(corpusDir, tool, summary);
    }
}

// Print overall summary
Console.WriteLine();
Console.WriteLine("============================================================");
Console.WriteLine("Diffra Regression Suite Summary");
Console.WriteLine("============================================================");
Console.WriteLine($"Corpora Evaluated: {corpora.Count}");
Console.WriteLine($"Total Cases:       {summary.TotalCases}");
Console.WriteLine($"Passed Cases:      {summary.PassedCases}");
Console.WriteLine($"Declared Breaks:   {summary.DeclaredBreaks.Count}");
Console.WriteLine($"Failed Cases:      {summary.Failures.Count}");
Console.WriteLine($"Backward Reads:    {(summary.BackwardReadsPassed ? "PASSED" : "N/A or FAILED")}");
Console.WriteLine("============================================================");

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

Console.WriteLine("All regression checks passed.");
return 0;

static void PrintUsage()
{
    Console.WriteLine("Usage: diffra-regression [mode] [options]");
    Console.WriteLine("Modes:");
    Console.WriteLine("  --tool <path>               Run candidate build against all corpora");
    Console.WriteLine("  --compare --old <old> --new <new>  Compare two tool builds against all corpora");
    Console.WriteLine("  generate <version> --tool <path>   Generate a new frozen corpus for a release");
    Console.WriteLine("Options:");
    Console.WriteLine("  --corpus <path>             Path to corpus directory (default: tests/regression)");
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
