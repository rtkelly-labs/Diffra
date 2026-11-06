using System.Diagnostics;
using System.Text;

var repoRoot = FindRepositoryRoot();
var baselinesDir = Path.Combine(repoRoot, "baselines", "dogfood");
var workDir = Path.Combine(repoRoot, "dogfood-run");
Directory.CreateDirectory(workDir);

var cliDll = Path.Combine(repoRoot, "src", "Cli", "bin", "Release", "net10.0", "Diffra.Cli.dll");
if (!File.Exists(cliDll))
{
    Console.WriteLine("Building Diffra.Cli in Release mode...");
    RunProcess("dotnet", "build", Path.Combine(repoRoot, "src", "Cli", "Diffra.Cli.csproj"), "-c", "Release");
}

Console.WriteLine("=== Diffra Dogfooding Execution ===");

// 1. Collect candidate metrics
Console.WriteLine("1. Collecting repository metrics...");
var candidatePayload = Path.Combine(workDir, "candidate-payload.json");
RunProcess("dotnet", "run", Path.Combine(repoRoot, "scripts", "DogfoodMetrics.cs"), "--", candidatePayload);

// 2. Collect candidate evidence
Console.WriteLine("2. Producing candidate Evidence document...");
var candidateEvidence = Path.Combine(workDir, "candidate-evidence.json");
var commit = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "1111111111111111111111111111111111111111";
RunProcess("dotnet", cliDll, "collect",
    "--payload", candidatePayload,
    "--subject-id", "diffra/core",
    "--commit", commit,
    "--type-id", "diffra.metrics.dogfood",
    "--type-version", "1.0",
    "--producer-id", "diffra.dogfood",
    "--producer-version", "0.1.0",
    "-o", candidateEvidence);

// 3. Diff against baseline
Console.WriteLine("3. Computing typed Delta against merge-base baseline...");
var baselineRef = Path.Combine(baselinesDir, "baseline-reference.json");
var baselineEvidence = Path.Combine(baselinesDir, "baseline-evidence.json");
var deltaPath = Path.Combine(workDir, "delta.json");

RunProcess("dotnet", cliDll, "diff",
    "--baseline-ref", baselineRef,
    "--baseline-evidence", baselineEvidence,
    "--evidence", candidateEvidence,
    "-o", deltaPath);

// 4. Evaluate Delta against policy rules
Console.WriteLine("4. Evaluating Delta against quality rules...");
var rulesPath = Path.Combine(baselinesDir, "rules.json");
var assessmentPath = Path.Combine(workDir, "assessment.json");

var evalExitCode = RunProcessAllowExitCode("dotnet", cliDll, "eval",
    "--rules", rulesPath,
    "--delta", deltaPath,
    "-o", assessmentPath);

// 5. Materialize review bundle (with Design System styling)
Console.WriteLine("5. Materializing presentation review bundle...");
var bundleDir = Path.Combine(workDir, "review-bundle");
RunProcess("dotnet", cliDll, "present", "bundle",
    "--delta", deltaPath,
    "--assessment", assessmentPath,
    "--baseline-ref", baselineRef,
    "-o", bundleDir);

// 6. Generate Markdown summary
Console.WriteLine("6. Generating review summary report...");
var markdownReport = CaptureOutput("dotnet", cliDll, "report",
    deltaPath,
    "--assessment", assessmentPath,
    "--baseline-ref", baselineRef,
    "--format", "markdown");

Console.WriteLine("\n" + markdownReport);

var stepSummaryFile = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
if (!string.IsNullOrEmpty(stepSummaryFile) && File.Exists(stepSummaryFile))
{
    File.AppendAllText(stepSummaryFile, "\n\n## 🐕 Diffra Self-Evaluation (Dogfood)\n\n" + markdownReport);
    Console.WriteLine($"Appended report summary to $GITHUB_STEP_SUMMARY ({stepSummaryFile}).");
}

if (evalExitCode != 0)
{
    Console.Error.WriteLine($"\n[FAIL] Policy evaluation exited with code {evalExitCode}. Gating failed.");
    Environment.Exit(evalExitCode);
}

Console.WriteLine("\n[SUCCESS] Diffra dogfooding run passed all quality policies!");

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
    var exitCode = RunProcessAllowExitCode(filename, args);
    if (exitCode != 0)
    {
        throw new InvalidOperationException($"Command failed with exit code {exitCode}.");
    }
}

static int RunProcessAllowExitCode(string filename, params string[] args)
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
    var outText = proc.StandardOutput.ReadToEnd();
    proc.WaitForExit();
    if (!string.IsNullOrWhiteSpace(err) && proc.ExitCode != 0)
    {
        Console.Error.WriteLine(err);
    }
    return proc.ExitCode;
}

static string CaptureOutput(string filename, params string[] args)
{
    var start = new ProcessStartInfo(filename)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    foreach (var arg in args) start.ArgumentList.Add(arg);
    using var proc = Process.Start(start)!;
    var stdout = proc.StandardOutput.ReadToEnd();
    proc.WaitForExit();
    return stdout;
}
