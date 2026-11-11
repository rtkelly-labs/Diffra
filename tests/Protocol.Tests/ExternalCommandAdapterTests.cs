using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Diffra.Protocol;

namespace Diffra.Protocol.Tests;

public static class ExternalCommandAdapterTests
{
    public static void RunAll()
    {
        ExternalCommandAdapterAndCrossEcosystemParity().GetAwaiter().GetResult();
        ExternalCommandAdapterRejectsInvalidPayloads().GetAwaiter().GetResult();
        ExternalCommandAdapterEmitsOpaqueEvidenceWithoutValidator().GetAwaiter().GetResult();
        ExternalCommandAdapterHashesInvocationConfig().GetAwaiter().GetResult();
        ExternalCommandAdapterBoundsOutputFile().GetAwaiter().GetResult();
        ExternalCommandAdapterKillsChildOnOutputOverflow().GetAwaiter().GetResult();
        ExternalCommandAdapterKillsChildOnCallerCancellation().GetAwaiter().GetResult();
        Console.WriteLine("PASS external command adapter");
    }

    private static async Task ExternalCommandAdapterAndCrossEcosystemParity()
    {
        var repoRoot = FindRepositoryRoot();
        var metricsScript = Path.Combine(repoRoot, "scripts", "DogfoodMetrics.cs");
        var tempFile = Path.Combine(Path.GetTempPath(), "diffra-ext-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            // 1. External Command Execution via OutputFilePath
            var options = new ExternalCommandOptions(
                Executable: "dotnet",
                Arguments: ["run", metricsScript, "--", tempFile],
                ExpectedTypeId: "diffra.metrics.dogfood",
                ExpectedTypeVersion: "1.0",
                SubjectId: "diffra/core",
                SubjectCommit: "4444444444444444444444444444444444444444",
                WorkingDirectory: repoRoot,
                OutputFilePath: tempFile,
                PayloadValidator: payload =>
                    payload.TryGetProperty("metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Object
                        ? null
                        : "payload must contain a 'metrics' object.");

            var result = await ExternalCommandAdapter.RunAsync(options).ConfigureAwait(false);

            Equal(0, result.ExitCode);
            Equal("recognized", ReadTypeResolution(result.EvidenceBytes));
            if (string.IsNullOrEmpty(result.EvidenceId))
            {
                throw new InvalidOperationException("External command adapter should have produced Evidence ID.");
            }
            EvidenceIdentity.Verify(result.EvidenceBytes);

            // 2. Cross-Ecosystem Parity: Verify in-process producer with same payload produces identical evidence
            var fileBytes = await File.ReadAllBytesAsync(tempFile).ConfigureAwait(false);
            var canonicalBytes = CanonicalJson.Canonicalize(fileBytes);

            var producerId = $"diffra.external-adapter.{Path.GetFileName(options.Executable)}";
            var (inProcessId, inProcessDigest, inProcessBytes) = EvidenceDocumentBuilder.Build(
                typeId: "diffra.metrics.dogfood",
                typeVersion: "1.0",
                subjectId: "diffra/core",
                commit: "4444444444444444444444444444444444444444",
                producerId: producerId,
                producerVersion: "1.0",
                canonicalPayloadBytes: canonicalBytes,
                platform: Environment.OSVersion.Platform.ToString(),
                runtime: Environment.Version.ToString(),
                typeResolution: "recognized",
                configDigest: ExternalCommandAdapter.ComputeConfigDigest(options));

            Equal(result.EvidenceId, inProcessId);
            Equal(result.PayloadDigest, inProcessDigest);
            Equal(result.EvidenceBytes.Length, inProcessBytes.Length);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private static async Task ExternalCommandAdapterRejectsInvalidPayloads()
    {
        if (OperatingSystem.IsWindows()) return;

        // A non-object root is never valid Evidence payload, whatever type it claims.
        await ThrowsAsync<InvalidDataException>(
            () => ExternalCommandAdapter.RunAsync(ShellOptions("printf null"))).ConfigureAwait(false);

        // A payload the type validator rejects is not emitted at all.
        var validated = ShellOptions("printf '{}'") with { PayloadValidator = _ => "missing required 'metrics' object." };
        var ex = await ThrowsAsync<InvalidDataException>(() => ExternalCommandAdapter.RunAsync(validated)).ConfigureAwait(false);
        if (!ex.Message.Contains("missing required 'metrics' object.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Validator error should be reported.");
        }
    }

    private static async Task ExternalCommandAdapterEmitsOpaqueEvidenceWithoutValidator()
    {
        if (OperatingSystem.IsWindows()) return;

        var opaque = await ExternalCommandAdapter.RunAsync(ShellOptions("printf '{}'")).ConfigureAwait(false);
        EvidenceIdentity.Verify(opaque.EvidenceBytes);
        Equal("opaque", ReadTypeResolution(opaque.EvidenceBytes));

        var recognized = await ExternalCommandAdapter.RunAsync(
            ShellOptions("printf '{}'") with { PayloadValidator = _ => null }).ConfigureAwait(false);
        EvidenceIdentity.Verify(recognized.EvidenceBytes);
        Equal("recognized", ReadTypeResolution(recognized.EvidenceBytes));
    }

    private static async Task ExternalCommandAdapterHashesInvocationConfig()
    {
        if (OperatingSystem.IsWindows()) return;

        var first = ShellOptions("printf '{}'", "first");
        var second = ShellOptions("printf '{}'", "second");

        var firstRun = await ExternalCommandAdapter.RunAsync(first).ConfigureAwait(false);
        var repeatRun = await ExternalCommandAdapter.RunAsync(first).ConfigureAwait(false);
        var secondRun = await ExternalCommandAdapter.RunAsync(second).ConfigureAwait(false);

        // The same invocation is deterministic; a different invocation is a different producer configuration.
        Equal(firstRun.EvidenceId, repeatRun.EvidenceId);
        Equal(firstRun.PayloadDigest, secondRun.PayloadDigest);
        if (string.Equals(firstRun.EvidenceId, secondRun.EvidenceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Different invocation arguments must yield different Evidence IDs.");
        }

        var configDigest = ReadConfigDigest(firstRun.EvidenceBytes);
        Equal(ExternalCommandAdapter.ComputeConfigDigest(first), configDigest);
        if (string.Equals(EvidenceDocumentBuilder.EmptyConfigDigest, configDigest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Adapter must not emit the empty config digest.");
        }

        // Environment entries are part of the configuration.
        var withEnvironment = first with { EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal) { ["DIFFRA_MODE"] = "strict" } };
        if (string.Equals(ExternalCommandAdapter.ComputeConfigDigest(first), ExternalCommandAdapter.ComputeConfigDigest(withEnvironment), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Environment variables must contribute to the config digest.");
        }
    }

    private static async Task ExternalCommandAdapterBoundsOutputFile()
    {
        if (OperatingSystem.IsWindows()) return;

        var outputFile = Path.Combine(Path.GetTempPath(), "diffra-ext-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var options = ShellOptions(
                "printf '{\"padding\":\"0123456789012345678901234567890123456789\"}' > \"$0\"",
                outputFile) with
            {
                OutputFilePath = outputFile,
                MaxOutputBytes = 16,
            };

            var ex = await ThrowsAsync<InvalidOperationException>(() => ExternalCommandAdapter.RunAsync(options)).ConfigureAwait(false);
            if (!ex.Message.Contains("output file exceeded", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Expected bounded output file failure, got: {ex.Message}");
            }
        }
        finally
        {
            if (File.Exists(outputFile)) File.Delete(outputFile);
        }
    }

    private static async Task ExternalCommandAdapterKillsChildOnOutputOverflow()
    {
        if (OperatingSystem.IsWindows()) return;

        var pidFile = Path.Combine(Path.GetTempPath(), "diffra-pid-" + Guid.NewGuid().ToString("N"));
        try
        {
            // The child writes endlessly; the overflow must surface as such (not as a timeout) and kill it.
            var options = ShellOptions("echo $$ > \"$0\"; exec yes", pidFile) with
            {
                MaxOutputBytes = 1024,
                Timeout = TimeSpan.FromSeconds(30),
            };

            var ex = await ThrowsAsync<InvalidOperationException>(() => ExternalCommandAdapter.RunAsync(options)).ConfigureAwait(false);
            if (!ex.Message.Contains("exceeded maximum bounded buffer limit", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Expected output overflow failure, got: {ex.Message}");
            }

            AssertProcessExited(pidFile);
        }
        finally
        {
            if (File.Exists(pidFile)) File.Delete(pidFile);
        }
    }

    private static async Task ExternalCommandAdapterKillsChildOnCallerCancellation()
    {
        if (OperatingSystem.IsWindows()) return;

        var pidFile = Path.Combine(Path.GetTempPath(), "diffra-pid-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = ShellOptions("echo $$ > \"$0\"; exec sleep 30", pidFile) with { Timeout = TimeSpan.FromSeconds(60) };
            using var callerCts = new CancellationTokenSource();
            var run = ExternalCommandAdapter.RunAsync(options, callerCts.Token);

            // Wait until the child has recorded its PID before cancelling.
            for (var attempt = 0; attempt < 100 && !HasPid(pidFile); attempt++)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }

            await Task.Delay(200).ConfigureAwait(false);
            await callerCts.CancelAsync().ConfigureAwait(false);

            await ThrowsAsync<OperationCanceledException>(() => run).ConfigureAwait(false);
            AssertProcessExited(pidFile);
        }
        finally
        {
            if (File.Exists(pidFile)) File.Delete(pidFile);
        }
    }

    private static ExternalCommandOptions ShellOptions(string script, params string[] positionalArguments)
    {
        return new ExternalCommandOptions(
            Executable: "/bin/sh",
            Arguments: ["-c", script, .. positionalArguments],
            ExpectedTypeId: "org.example.payload",
            ExpectedTypeVersion: "1.0",
            SubjectId: "diffra/core",
            SubjectCommit: "7777777777777777777777777777777777777777",
            Timeout: TimeSpan.FromSeconds(30));
    }

    private static bool HasPid(string pidFile)
    {
        return File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0;
    }

    private static void AssertProcessExited(string pidFile)
    {
        var pid = int.Parse(File.ReadAllText(pidFile).Trim(), CultureInfo.InvariantCulture);
        try
        {
            using var child = Process.GetProcessById(pid);
            if (!child.HasExited)
            {
                throw new InvalidOperationException($"Child process {pid} is still running after the adapter failed.");
            }
        }
        catch (ArgumentException)
        {
            // No process with that ID is running.
        }
    }

    private static string ReadTypeResolution(byte[] evidenceBytes)
    {
        using var document = JsonDocument.Parse(evidenceBytes);
        return document.RootElement.GetProperty("typeResolution").GetString()!;
    }

    private static string ReadConfigDigest(byte[] evidenceBytes)
    {
        using var document = JsonDocument.Parse(evidenceBytes);
        return document.RootElement.GetProperty("producer").GetProperty("configDigest").GetString()!;
    }

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException ex)
        {
            return ex;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} to be thrown.");
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Diffra.slnx"))) return dir.FullName;
        }
        return Directory.GetCurrentDirectory();
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected: {expected}, Actual: {actual}");
        }
    }
}
