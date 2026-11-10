using System.Diagnostics;
using System.Security.Cryptography;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: dotnet run scripts/PublishBaseline.cs <evidence-file> <commit-sha>");
    Environment.Exit(1);
    return;
}

var evidenceFile = Path.GetFullPath(args[0]);
var commitSha = args[1].Trim();

if (!File.Exists(evidenceFile) || new FileInfo(evidenceFile).Length == 0)
{
    Console.Error.WriteLine($"[FAIL] Evidence file not found or empty: {evidenceFile}");
    Environment.Exit(1);
    return;
}

if (string.IsNullOrWhiteSpace(commitSha))
{
    Console.Error.WriteLine("[FAIL] Commit SHA must not be empty.");
    Environment.Exit(1);
    return;
}

var evidenceBytes = File.ReadAllBytes(evidenceFile);
var targetTempDir = Path.Combine(Path.GetTempPath(), "diffra-baselines-publish-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(targetTempDir);

var remoteUrl = Environment.GetEnvironmentVariable("DIFFRA_BASELINES_REMOTE");
if (string.IsNullOrWhiteSpace(remoteUrl))
{
    var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    var repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
    if (!string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(repo))
    {
        remoteUrl = $"https://x-access-token:{token}@github.com/{repo}.git";
    }
    else
    {
        remoteUrl = "origin";
    }
}

try
{
    // Check if remote branch diffra-baselines exists
    var (lsExit, lsOut, lsErr) = RunProcess("git", "ls-remote", "--heads", remoteUrl, "diffra-baselines");
    if (lsExit != 0)
    {
        Console.Error.WriteLine($"[FAIL] git ls-remote failed with exit code {lsExit}: {lsErr}");
        Environment.Exit(lsExit);
        return;
    }

    bool branchExists = !string.IsNullOrWhiteSpace(lsOut);

    if (!branchExists)
    {
        Console.WriteLine("[PUBLISH] Remote diffra-baselines branch does not exist yet. Initializing orphan branch...");
        var initTree = Path.Combine(targetTempDir, "init-branch");
        Directory.CreateDirectory(initTree);

        RunProcessInDir(initTree, "git", "init", "-b", "diffra-baselines");
        ConfigureGitUser(initTree);

        var evidenceDir = Path.Combine(initTree, "evidence");
        Directory.CreateDirectory(evidenceDir);
        var targetFile = Path.Combine(evidenceDir, $"{commitSha}.json");
        File.WriteAllBytes(targetFile, evidenceBytes);

        RunProcessInDir(initTree, "git", "add", $"evidence/{commitSha}.json");
        RunProcessInDir(initTree, "git", "commit", "-m", $"baseline: record evidence for {commitSha}");
        var (pushExit, _, pushErr) = RunProcessInDir(initTree, "git", "push", "-u", remoteUrl, "diffra-baselines");
        if (pushExit == 0)
        {
            Console.WriteLine($"[SUCCESS] Initialized diffra-baselines branch and published baseline for {commitSha}.");
            return;
        }

        Console.WriteLine($"[PUBLISH] Initial push encountered conflict ({pushErr.Trim()}). Retrying via clone/rebase...");
    }

    // Branch exists (or raced during init) - clone and push with up to 3 retry attempts
    const int maxAttempts = 3;
    for (int attempt = 1; attempt <= maxAttempts; attempt++)
    {
        Console.WriteLine($"[PUBLISH] Attempt {attempt}/{maxAttempts}: Cloning diffra-baselines...");
        var workTree = Path.Combine(targetTempDir, $"clone-{attempt}");
        Directory.CreateDirectory(workTree);

        var (cloneExit, _, cloneErr) = RunProcess("git", "clone", "--branch", "diffra-baselines", "--single-branch", remoteUrl, workTree);
        if (cloneExit != 0)
        {
            Console.Error.WriteLine($"[PUBLISH] Clone attempt {attempt} failed: {cloneErr.Trim()}");
            if (attempt == maxAttempts)
            {
                Environment.Exit(cloneExit);
                return;
            }
            Thread.Sleep(2000);
            continue;
        }

        ConfigureGitUser(workTree);
        var evidenceDir = Path.Combine(workTree, "evidence");
        Directory.CreateDirectory(evidenceDir);
        var targetFile = Path.Combine(evidenceDir, $"{commitSha}.json");

        if (File.Exists(targetFile))
        {
            var existingBytes = File.ReadAllBytes(targetFile);
            if (CryptographicOperations.FixedTimeEquals(existingBytes, evidenceBytes))
            {
                Console.WriteLine($"[SUCCESS] Baseline Evidence for {commitSha} already exists with identical content. Idempotent success.");
                return;
            }
        }

        File.WriteAllBytes(targetFile, evidenceBytes);
        RunProcessInDir(workTree, "git", "add", $"evidence/{commitSha}.json");

        var (statusExit, statusOut, _) = RunProcessInDir(workTree, "git", "status", "--porcelain");
        if (statusExit == 0 && string.IsNullOrWhiteSpace(statusOut))
        {
            Console.WriteLine($"[SUCCESS] Nothing to commit for {commitSha}. Idempotent success.");
            return;
        }

        RunProcessInDir(workTree, "git", "commit", "-m", $"baseline: record evidence for {commitSha}");

        var (pushCode, _, pushError) = RunProcessInDir(workTree, "git", "push", "origin", "diffra-baselines");
        if (pushCode == 0)
        {
            Console.WriteLine($"[SUCCESS] Recorded baseline Evidence for {commitSha} on diffra-baselines.");
            return;
        }

        Console.WriteLine($"[PUBLISH] Push rejected on attempt {attempt}: {pushError.Trim()}. Attempting fetch + rebase...");
        var (fetchExit, _, _) = RunProcessInDir(workTree, "git", "fetch", "origin", "diffra-baselines");
        if (fetchExit == 0)
        {
            var (rebaseExit, _, rebaseErr) = RunProcessInDir(workTree, "git", "rebase", "origin/diffra-baselines");
            if (rebaseExit == 0)
            {
                var (retryPushExit, _, retryPushErr) = RunProcessInDir(workTree, "git", "push", "origin", "diffra-baselines");
                if (retryPushExit == 0)
                {
                    Console.WriteLine($"[SUCCESS] Recorded baseline Evidence for {commitSha} after rebase.");
                    return;
                }
                Console.WriteLine($"[PUBLISH] Push after rebase failed: {retryPushErr.Trim()}");
            }
            else
            {
                Console.WriteLine($"[PUBLISH] Rebase conflict or failure: {rebaseErr.Trim()}");
                RunProcessInDir(workTree, "git", "rebase", "--abort");
            }
        }

        if (attempt == maxAttempts)
        {
            Console.Error.WriteLine("[FAIL] Exceeded maximum retry attempts pushing baseline.");
            Environment.Exit(1);
            return;
        }

        Thread.Sleep(2000);
    }
}
finally
{
    try
    {
        if (Directory.Exists(targetTempDir))
        {
            Directory.Delete(targetTempDir, recursive: true);
        }
    }
    catch
    {
        // ignored
    }
}

static void ConfigureGitUser(string dir)
{
    RunProcessInDir(dir, "git", "config", "user.name", "github-actions[bot]");
    RunProcessInDir(dir, "git", "config", "user.email", "github-actions[bot]@users.noreply.github.com");
}

static (int exitCode, string stdout, string stderr) RunProcess(string filename, params string[] args)
{
    return RunProcessInDir(null, filename, args);
}

static (int exitCode, string stdout, string stderr) RunProcessInDir(string? workingDir, string filename, params string[] args)
{
    var start = new ProcessStartInfo(filename)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    if (!string.IsNullOrEmpty(workingDir))
    {
        start.WorkingDirectory = workingDir;
    }
    foreach (var arg in args) start.ArgumentList.Add(arg);
    using var proc = Process.Start(start)!;
    var stdoutTask = proc.StandardOutput.ReadToEndAsync();
    var stderrTask = proc.StandardError.ReadToEndAsync();
    proc.WaitForExit();
    var stdout = stdoutTask.GetAwaiter().GetResult();
    var stderr = stderrTask.GetAwaiter().GetResult();
    return (proc.ExitCode, stdout, stderr);
}
