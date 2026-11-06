using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Diffra.Protocol;

/// <summary>Configuration options for executing an external command evidence producer.</summary>
/// <remarks>
/// <see cref="PayloadValidator"/> checks the payload against the declared type version's schema and
/// returns an error message, or <see langword="null"/> when the payload is valid. Without a validator
/// the adapter cannot recognize the payload, so it emits <c>opaque</c> Evidence.
/// </remarks>
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
    string? OutputFilePath = null,
    Func<JsonElement, string?>? PayloadValidator = null);

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
        var exitTask = process.WaitForExitAsync(cts.Token);

        try
        {
            // Observe exit and each reader as it finishes, so an output overflow on either stream
            // surfaces while the child is still running instead of after it exits or times out.
            var pending = new List<Task> { exitTask, stdoutTask, stderrTask };
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending).ConfigureAwait(false);
                await finished.ConfigureAwait(false);
                pending.Remove(finished);
            }
        }
        catch (Exception ex)
        {
            // Every failure path (timeout, caller cancellation, output overflow) terminates the child.
            await TerminateAsync(process).ConfigureAwait(false);
            if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"External process '{options.Executable}' exceeded timeout of {timeout.TotalSeconds:F0}s.", ex);
            }

            throw;
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
            payloadBytes = await ReadBoundedFileAsync(options.OutputFilePath, options.MaxOutputBytes, cancellationToken).ConfigureAwait(false);
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
        if (jsonDoc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"External command payload must be a JSON object, but its root is {jsonDoc.RootElement.ValueKind}.");
        }

        var typeResolution = EvidenceDocumentBuilder.OpaqueTypeResolution;
        if (options.PayloadValidator is not null)
        {
            var validationError = options.PayloadValidator(jsonDoc.RootElement);
            if (validationError is not null)
            {
                throw new InvalidDataException(
                    $"External command payload is not valid for type '{options.ExpectedTypeId}' version '{options.ExpectedTypeVersion}': {validationError}");
            }

            typeResolution = EvidenceDocumentBuilder.RecognizedTypeResolution;
        }

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
            runtime: Environment.Version.ToString(),
            typeResolution: typeResolution,
            configDigest: ComputeConfigDigest(options));

        return new ExternalCommandResult(
            ExitCode: process.ExitCode,
            Stdout: stdoutText,
            Stderr: stderrText,
            Duration: sw.Elapsed,
            EvidenceId: id,
            PayloadDigest: digest,
            EvidenceBytes: evidenceBytes);
    }

    /// <summary>
    /// Computes the producer configuration digest for an invocation: the SHA-256 of the canonical JSON
    /// object <c>{executable, arguments, environment}</c>, with environment entries sorted by name.
    /// The working directory, output file location, and timeout are machine-specific and excluded.
    /// </summary>
    public static string ComputeConfigDigest(ExternalCommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var arguments = new JsonArray();
        foreach (var arg in options.Arguments)
        {
            arguments.Add(JsonValue.Create(arg));
        }

        var environment = new JsonObject();
        if (options.EnvironmentVariables != null)
        {
            foreach (var (name, value) in options.EnvironmentVariables.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                environment[name] = value;
            }
        }

        var config = new JsonObject
        {
            ["executable"] = options.Executable,
            ["arguments"] = arguments,
            ["environment"] = environment,
        };

        return EvidenceIdentity.ComputePayloadDigest(Encoding.UTF8.GetBytes(config.ToJsonString()));
    }

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The process could not be terminated; the bounded wait below still applies.
        }

        await Task.WhenAny(
            process.WaitForExitAsync(CancellationToken.None),
            Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedFileAsync(string path, int maxBytes, CancellationToken ct)
    {
        if (new FileInfo(path).Length > maxBytes)
        {
            throw new InvalidOperationException($"External command output file exceeded maximum bounded buffer limit of {maxBytes} bytes.");
        }

        // The file can grow between the length check and the read, so bound the read itself as well.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        using var destination = new MemoryStream();
        var buffer = new byte[4096];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidOperationException($"External command output file exceeded maximum bounded buffer limit of {maxBytes} bytes.");
            }

            destination.Write(buffer, 0, read);
        }

        return destination.ToArray();
    }

    private static async Task ReadBoundedStreamAsync(
        StreamReader reader,
        StringBuilder destination,
        int maxBytes,
        CancellationToken ct)
    {
        char[] buffer = new char[4096];
        long totalBytes = 0;
        int read;

        while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            totalBytes += Encoding.UTF8.GetByteCount(buffer.AsSpan(0, read));
            if (totalBytes > maxBytes)
            {
                throw new InvalidOperationException($"External command output exceeded maximum bounded buffer limit of {maxBytes} bytes.");
            }
            destination.Append(buffer, 0, read);
        }
    }
}
