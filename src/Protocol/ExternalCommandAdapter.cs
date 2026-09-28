using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Diffra.Protocol;

/// <summary>Configuration options for executing an external command evidence producer.</summary>
public sealed record ExternalCommandOptions(
    string Executable,
    IReadOnlyList<string> Arguments,
    string ExpectedTypeId,
    string ExpectedTypeVersion,
    string SubjectId,
    string SubjectCommit,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    TimeSpan? Timeout = null,
    int MaxOutputBytes = 10 * 1024 * 1024,
    string? OutputFilePath = null);

/// <summary>Result of executing an external command adapter.</summary>
public sealed record ExternalCommandResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration,
    string EvidenceId,
    string PayloadDigest,
    byte[] EvidenceBytes);

/// <summary>Adapter for executing external commands and validating bounded evidence outputs.</summary>
public static class ExternalCommandAdapter
{
    public static async Task<ExternalCommandResult> RunAsync(
        ExternalCommandOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExpectedTypeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExpectedTypeVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SubjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SubjectCommit);

        var startInfo = new ProcessStartInfo(options.Executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrEmpty(options.WorkingDirectory))
        {
            startInfo.WorkingDirectory = options.WorkingDirectory;
        }

        foreach (var arg in options.Arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (options.EnvironmentVariables != null)
        {
            foreach (var (k, v) in options.EnvironmentVariables)
            {
                startInfo.Environment[k] = v;
            }
        }

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start external process '{options.Executable}'.");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not execute '{options.Executable}': {ex.Message}", ex);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = options.Timeout ?? TimeSpan.FromSeconds(60);
        cts.CancelAfter(timeout);

        var stdoutTask = ReadBoundedStreamAsync(process.StandardOutput, stdoutBuilder, options.MaxOutputBytes, cts.Token);
        var stderrTask = ReadBoundedStreamAsync(process.StandardError, stderrBuilder, options.MaxOutputBytes, cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new TimeoutException($"External process '{options.Executable}' exceeded timeout of {timeout.TotalSeconds:F0}s.");
        }

        sw.Stop();

        var stdoutText = stdoutBuilder.ToString();
        var stderrText = stderrBuilder.ToString();

        if (process.ExitCode != 0)
        {
            var errMsg = string.IsNullOrWhiteSpace(stderrText) ? stdoutText : stderrText;
            throw new InvalidOperationException($"External process '{options.Executable}' exited with code {process.ExitCode}: {errMsg.Trim()}");
        }

        byte[] payloadBytes;
        if (!string.IsNullOrEmpty(options.OutputFilePath))
        {
            if (!File.Exists(options.OutputFilePath))
            {
                throw new FileNotFoundException($"External process did not produce declared output file at '{options.OutputFilePath}'.");
            }
            payloadBytes = await File.ReadAllBytesAsync(options.OutputFilePath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            payloadBytes = Encoding.UTF8.GetBytes(stdoutText);
        }

        if (payloadBytes.Length == 0)
        {
            throw new InvalidOperationException("External command produced empty output payload.");
        }

        // Validate that the payload is valid canonical JSON
        using var jsonDoc = CanonicalJson.Parse(payloadBytes);
        var canonicalBytes = CanonicalJson.Canonicalize(payloadBytes);

        var producerId = $"diffra.external-adapter.{Path.GetFileName(options.Executable)}";
        var (id, digest, evidenceBytes) = EvidenceDocumentBuilder.Build(
            typeId: options.ExpectedTypeId,
            typeVersion: options.ExpectedTypeVersion,
            subjectId: options.SubjectId,
            commit: options.SubjectCommit,
            producerId: producerId,
            producerVersion: "1.0",
            canonicalPayloadBytes: canonicalBytes,
            platform: Environment.OSVersion.Platform.ToString(),
            runtime: Environment.Version.ToString());

        return new ExternalCommandResult(
            ExitCode: process.ExitCode,
            Stdout: stdoutText,
            Stderr: stderrText,
            Duration: sw.Elapsed,
            EvidenceId: id,
            PayloadDigest: digest,
            EvidenceBytes: evidenceBytes);
    }

    private static async Task ReadBoundedStreamAsync(
        StreamReader reader,
        StringBuilder destination,
        int maxBytes,
        CancellationToken ct)
    {
        char[] buffer = new char[4096];
        int totalChars = 0;
        int read;

        while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            totalChars += read;
            if (totalChars > maxBytes)
            {
                throw new InvalidOperationException($"External command output exceeded maximum bounded buffer limit of {maxBytes} bytes.");
            }
            destination.Append(buffer, 0, read);
        }
    }
}
