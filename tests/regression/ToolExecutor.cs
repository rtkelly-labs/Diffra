using System.Diagnostics;

namespace Diffra.Regression;

public static class ToolExecutor
{
    public static (int ExitCode, string StandardError) Run(
        string tool,
        IReadOnlyList<string> args,
        string? workingDirectory = null,
        bool allowPath = false)
    {
        var (code, _, err) = ExecuteProcess(tool, args, workingDirectory, allowPath);
        return (code, err);
    }

    public static (int ExitCode, string StandardOutput, string StandardError) RunWithOutput(
        string tool,
        IReadOnlyList<string> args,
        string? workingDirectory = null,
        bool allowPath = false)
    {
        return ExecuteProcess(tool, args, workingDirectory, allowPath);
    }

    public static string InspectToolVersion(string tool, bool allowPath = false)
    {
        var fullToolPath = tool.Contains('/') || tool.Contains('\\') ? Path.GetFullPath(tool) : tool;
        if (File.Exists(fullToolPath))
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(fullToolPath);
                if (!string.IsNullOrWhiteSpace(vi.ProductVersion))
                {
                    return vi.ProductVersion;
                }
            }
            catch
            {
                // Ignore failure reading version info
            }
        }

        // If it's a .NET tool shim, check .store directory next to tool
        var dir = Path.GetDirectoryName(fullToolPath);
        if (!string.IsNullOrEmpty(dir))
        {
            var storeDir = Path.Combine(dir, ".store");
            if (Directory.Exists(storeDir))
            {
                var dll = Directory.EnumerateFiles(storeDir, "Diffra.Cli.dll", SearchOption.AllDirectories).FirstOrDefault();
                if (dll != null)
                {
                    try
                    {
                        var vi = FileVersionInfo.GetVersionInfo(dll);
                        if (!string.IsNullOrWhiteSpace(vi.ProductVersion))
                        {
                            return vi.ProductVersion;
                        }
                    }
                    catch
                    {
                        // Ignore
                    }
                }
            }
        }

        // Attempt running tool with --version
        try
        {
            var (code, stdout, stderr) = RunWithOutput(tool, ["--version"], allowPath: allowPath);
            var text = !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim() : stderr.Trim();
            if (!string.IsNullOrWhiteSpace(text) && !text.StartsWith("Unknown command", StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }
        }
        catch
        {
            // Ignore
        }

        return "unknown";
    }

    public static bool IsGitTracked(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            var psi = new ProcessStartInfo("git", $"ls-files --error-unmatch \"{filePath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = string.IsNullOrEmpty(dir) ? Directory.GetCurrentDirectory() : dir
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (int ExitCode, string StandardOutput, string StandardError) ExecuteProcess(
        string tool,
        IReadOnlyList<string> args,
        string? workingDirectory,
        bool allowPath)
    {
        if (!tool.Contains('/') && !tool.Contains('\\') && !allowPath)
        {
            throw new ArgumentException($"Bare tool name '{tool}' is not permitted without --allow-path. Specify a relative or absolute file path or pass --allow-path.");
        }

        var fullToolPath = tool.Contains('/') || tool.Contains('\\') ? Path.GetFullPath(tool) : tool;

        if (OperatingSystem.IsWindows() && !File.Exists(fullToolPath) && File.Exists(fullToolPath + ".exe"))
        {
            fullToolPath += ".exe";
        }

        if (!allowPath && !File.Exists(fullToolPath))
        {
            throw new FileNotFoundException($"Tool executable not found at '{fullToolPath}'.");
        }

        var psi = new ProcessStartInfo();
        if (fullToolPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "dotnet";
            psi.ArgumentList.Add(fullToolPath);
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
        }
        else if (fullToolPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "dotnet";
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("--project");
            psi.ArgumentList.Add(fullToolPath);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("Release");
            psi.ArgumentList.Add("--no-build");
            psi.ArgumentList.Add("--");
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
        }
        else
        {
            // Only fix permissions on directly executed binary if it is an untracked file (e.g. extracted archive)
            if (!OperatingSystem.IsWindows() && File.Exists(fullToolPath) && !IsGitTracked(fullToolPath))
            {
                try
                {
                    var mode = File.GetUnixFileMode(fullToolPath);
                    if (!mode.HasFlag(UnixFileMode.UserExecute))
                    {
                        File.SetUnixFileMode(fullToolPath, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                    }
                }
                catch
                {
                    // Ignore if filesystem permissions cannot be updated
                }
            }

            psi.FileName = fullToolPath;
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
        }

        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }

        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start process: {tool}");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout, stderr);
    }
}
