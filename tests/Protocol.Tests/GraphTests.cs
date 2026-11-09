using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diffra.Protocol;

namespace Diffra.Protocol.Tests;

public static class GraphTests
{
    public static void RunAll()
    {
        ValidGraphExecutesInTopologicalOrder().GetAwaiter().GetResult();
        GraphValidatorRejectsCycles();
        GraphValidatorRejectsMissingInputs();
        GraphValidatorRejectsIncompatibleTypes();
        GraphValidatorRejectsImpossibleBaselineRoles();
        GraphValidatorRejectsTrustedAsBaselineRole();
        GraphValidatorRejectsTypeVersionMismatchBeforeExecution().GetAwaiter().GetResult();
        GraphValidatorRejectsTypedConsumerOfUntypedNode().GetAwaiter().GetResult();
        CascadingFaultIsolation().GetAwaiter().GetResult();
        ExternalCommandAdapterAndCrossEcosystemParity().GetAwaiter().GetResult();
        ExternalCommandAdapterRejectsInvalidPayloads().GetAwaiter().GetResult();
        ExternalCommandAdapterEmitsOpaqueEvidenceWithoutValidator().GetAwaiter().GetResult();
        ExternalCommandAdapterHashesInvocationConfig().GetAwaiter().GetResult();
        ExternalCommandAdapterBoundsOutputFile().GetAwaiter().GetResult();
        ExternalCommandAdapterKillsChildOnOutputOverflow().GetAwaiter().GetResult();
        ExternalCommandAdapterKillsChildOnCallerCancellation().GetAwaiter().GetResult();
        Console.WriteLine("PASS DAG composition host, strict validation, cascading fault isolation, and external adapter");
    }

    private static async Task ValidGraphExecutesInTopologicalOrder()
    {
        var executionLog = new List<string>();

        var builder = new GraphBuilder();
        builder.RegisterSubject("subject.core", (ctx, ct) =>
        {
            executionLog.Add("subject.core");
            return Task.FromResult<object?>("commit-1234");
        });

        builder.RegisterProducer("producer.metrics", "diffra.metrics", "1.0", ["subject.core"], (ctx, ct) =>
        {
            executionLog.Add("producer.metrics");
            var commit = ctx.GetResult<string>("subject.core");
            return Task.FromResult<object?>($"metrics-for-{commit}");
        });

        builder.RegisterBaseline("baseline.merge-base", "diffra.metrics", "1.0", "merge-base", ["subject.core"], (ctx, ct) =>
        {
            executionLog.Add("baseline.merge-base");
            return Task.FromResult<object?>("baseline-metrics");
        });

        builder.RegisterComparator("comparator.numeric", "diffra.metrics", "1.0", "diffra.delta", "1.0", ["producer.metrics", "baseline.merge-base"], (ctx, ct) =>
        {
            executionLog.Add("comparator.numeric");
            var cand = ctx.GetResult<string>("producer.metrics");
            var @base = ctx.GetResult<string>("baseline.merge-base");
            return Task.FromResult<object?>($"delta: {cand} vs {@base}");
        });

        builder.RegisterPolicy("policy.budget", "diffra.delta", "1.0", "diffra.assessment", "1.0", ["comparator.numeric"], (ctx, ct) =>
        {
            executionLog.Add("policy.budget");
            var delta = ctx.GetResult<string>("comparator.numeric");
            return Task.FromResult<object?>($"assessment: {delta} -> pass");
        });

        var graph = builder.Build();
        var report = await GraphExecutor.ExecuteAsync(graph).ConfigureAwait(false);

        if (!report.Success)
        {
            throw new InvalidOperationException("Graph execution should have succeeded.");
        }

        // Verify topological order
        int subjectIdx = executionLog.IndexOf("subject.core");
        int prodIdx = executionLog.IndexOf("producer.metrics");
        int baseIdx = executionLog.IndexOf("baseline.merge-base");
        int compIdx = executionLog.IndexOf("comparator.numeric");
        int policyIdx = executionLog.IndexOf("policy.budget");

        if (!(subjectIdx < prodIdx && subjectIdx < baseIdx && prodIdx < compIdx && baseIdx < compIdx && compIdx < policyIdx))
        {
            throw new InvalidOperationException($"Invalid execution order: {string.Join(" -> ", executionLog)}");
        }

        var finalAssessment = report.GetResult("policy.budget").Output as string;
        Equal("assessment: delta: metrics-for-commit-1234 vs baseline-metrics -> pass", finalAssessment);
    }

    private static void GraphValidatorRejectsCycles()
    {
        var builder = new GraphBuilder();
        builder.RegisterProducer("nodeA", "type.a", "1.0", ["nodeC"], (ctx, ct) => Task.FromResult<object?>("A"));
        builder.RegisterProducer("nodeB", "type.a", "1.0", ["nodeA"], (ctx, ct) => Task.FromResult<object?>("B"));
        builder.RegisterProducer("nodeC", "type.a", "1.0", ["nodeB"], (ctx, ct) => Task.FromResult<object?>("C"));

        var graph = builder.Build();
        try
        {
            GraphValidator.Validate(graph);
            throw new InvalidOperationException("Validator should have rejected cycle.");
        }
        catch (GraphValidationException ex)
        {
            if (!ex.Violations.Any(v => v.Contains("Cycle detected", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Expected cycle violation message.");
            }
        }
    }

    private static void GraphValidatorRejectsMissingInputs()
    {
        var builder = new GraphBuilder();
        builder.RegisterProducer("nodeA", "type.a", "1.0", ["ghost-node"], (ctx, ct) => Task.FromResult<object?>("A"));

        var graph = builder.Build();
        try
        {
            GraphValidator.Validate(graph);
            throw new InvalidOperationException("Validator should have rejected missing input.");
        }
        catch (GraphValidationException ex)
        {
            if (!ex.Violations.Any(v => v.Contains("missing node 'ghost-node'", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Expected missing node violation message.");
            }
        }
    }

    private static void GraphValidatorRejectsIncompatibleTypes()
    {
        var builder = new GraphBuilder();
        builder.RegisterProducer("nodeA", "diffra.type.a", "1.0", [], (ctx, ct) => Task.FromResult<object?>("A"));
        builder.RegisterComparator("nodeB", "diffra.type.b", "1.0", "diffra.delta", "1.0", ["nodeA"], (ctx, ct) => Task.FromResult<object?>("B"));

        var graph = builder.Build();
        try
        {
            GraphValidator.Validate(graph);
            throw new InvalidOperationException("Validator should have rejected type mismatch.");
        }
        catch (GraphValidationException ex)
        {
            if (!ex.Violations.Any(v => v.Contains("Type mismatch", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Expected type mismatch violation message.");
            }
        }
    }

    private static void GraphValidatorRejectsImpossibleBaselineRoles()
    {
        var builder = new GraphBuilder();
        builder.RegisterBaseline("baselineA", "diffra.type.a", "1.0", "arbitrary-role", [], (ctx, ct) => Task.FromResult<object?>("A"));

        var graph = builder.Build();
        try
        {
            GraphValidator.Validate(graph);
            throw new InvalidOperationException("Validator should have rejected impossible baseline role.");
        }
        catch (GraphValidationException ex)
        {
            if (!ex.Violations.Any(v => v.Contains("impossible role 'arbitrary-role'", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Expected impossible baseline role violation message.");
            }
        }
    }

    private static void GraphValidatorRejectsTrustedAsBaselineRole()
    {
        // "trusted" is a baseline trust status, not a baseline role.
        var builder = new GraphBuilder();
        builder.RegisterBaseline("baselineA", "diffra.type.a", "1.0", "trusted", [], (ctx, ct) => Task.FromResult<object?>("A"));

        try
        {
            GraphValidator.Validate(builder.Build());
            throw new InvalidOperationException("Validator should have rejected 'trusted' as a baseline role.");
        }
        catch (GraphValidationException ex)
        {
            if (!ex.Violations.Any(v => v.Contains("impossible role 'trusted'", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Expected impossible baseline role violation for 'trusted'.");
            }
        }
    }

    private static async Task GraphValidatorRejectsTypeVersionMismatchBeforeExecution()
    {
        var handlerRan = false;
        var builder = new GraphBuilder();
        builder.RegisterProducer("producer.metrics", "org.example.metrics", "1.0", [], (ctx, ct) =>
        {
            handlerRan = true;
            return Task.FromResult<object?>("metrics");
        });
        builder.RegisterComparator("comparator.metrics", "org.example.metrics", "2.0", "diffra.delta", "1.0", ["producer.metrics"], (ctx, ct) =>
        {
            handlerRan = true;
            return Task.FromResult<object?>("delta");
        });

        var ex = await ThrowsAsync<GraphValidationException>(() => GraphExecutor.ExecuteAsync(builder.Build())).ConfigureAwait(false);
        if (!ex.Violations.Any(v => v.Contains("Type version mismatch", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Expected type version mismatch violation message.");
        }

        Equal(false, handlerRan);
    }

    private static async Task GraphValidatorRejectsTypedConsumerOfUntypedNode()
    {
        var handlerRan = false;
        var builder = new GraphBuilder();
        builder.RegisterStore("store.cas", "diffra.metrics", "1.0", [], (ctx, ct) =>
        {
            handlerRan = true;
            return Task.FromResult<object?>("stored");
        });
        builder.RegisterComparator("comparator.metrics", "diffra.metrics", "1.0", "diffra.delta", "1.0", ["store.cas"], (ctx, ct) =>
        {
            handlerRan = true;
            return Task.FromResult<object?>("delta");
        });

        var ex = await ThrowsAsync<GraphValidationException>(() => GraphExecutor.ExecuteAsync(builder.Build())).ConfigureAwait(false);
        if (!ex.Violations.Any(v => v.Contains("upstream 'store.cas' declares no output type", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Expected untyped upstream violation message.");
        }

        Equal(false, handlerRan);
    }

    private static async Task CascadingFaultIsolation()
    {
        var builder = new GraphBuilder();

        // Independent node that should succeed
        builder.RegisterSubject("independent.node", (ctx, ct) => Task.FromResult<object?>("independent-ok"));

        // Upstream failing node
        builder.RegisterProducer("failing.producer", "diffra.data", "1.0", [], (ctx, ct) =>
        {
            throw new InvalidOperationException("Hardware or network glitch in producer.");
        });

        // Downstream dependent node that should be blocked
        builder.RegisterComparator("blocked.comparator", "diffra.data", "1.0", "diffra.delta", "1.0", ["failing.producer"], (ctx, ct) =>
        {
            return Task.FromResult<object?>("should never run");
        });

        // Downstream from blocked node that should also be blocked
        builder.RegisterPolicy("blocked.policy", "diffra.delta", "1.0", "diffra.assessment", "1.0", ["blocked.comparator"], (ctx, ct) =>
        {
            return Task.FromResult<object?>("should never run");
        });

        var graph = builder.Build();
        var report = await GraphExecutor.ExecuteAsync(graph).ConfigureAwait(false);

        if (report.Success)
        {
            throw new InvalidOperationException("Report should indicate failure.");
        }

        Equal(NodeExecutionStatus.Completed, report.GetResult("independent.node").Status);
        Equal(NodeExecutionStatus.Failed, report.GetResult("failing.producer").Status);
        Equal(NodeExecutionStatus.Blocked, report.GetResult("blocked.comparator").Status);
        Equal(NodeExecutionStatus.Blocked, report.GetResult("blocked.policy").Status);

        if (!report.GetResult("blocked.comparator").ErrorMessage!.Contains("failing.producer", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Blocked node should cite failed upstream dependency.");
        }
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
