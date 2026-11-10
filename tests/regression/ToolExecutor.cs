using System.Diagnostics;

namespace Diffra.Regression;

public static class ToolExecutor
{
    public static (int ExitCode, string StandardError) Run(
        string tool,
        IReadOnlyList<string> args,
        string? workingDirectory = null)
    {
        var fullToolPath = tool.Contains('/') || tool.Contains('\\') ? Path.GetFullPath(tool) : tool;
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
        _ = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stderr);
    }
}
