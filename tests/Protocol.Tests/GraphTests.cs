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
        CascadingFaultIsolation().GetAwaiter().GetResult();
        ExternalCommandAdapterAndCrossEcosystemParity().GetAwaiter().GetResult();
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

        builder.RegisterProducer("producer.metrics", "diffra.metrics", ["subject.core"], (ctx, ct) =>
        {
            executionLog.Add("producer.metrics");
            var commit = ctx.GetResult<string>("subject.core");
            return Task.FromResult<object?>($"metrics-for-{commit}");
        });

        builder.RegisterBaseline("baseline.merge-base", "diffra.metrics", "merge-base", ["subject.core"], (ctx, ct) =>
        {
            executionLog.Add("baseline.merge-base");
            return Task.FromResult<object?>("baseline-metrics");
        });

        builder.RegisterComparator("comparator.numeric", "diffra.metrics", "diffra.delta", ["producer.metrics", "baseline.merge-base"], (ctx, ct) =>
        {
            executionLog.Add("comparator.numeric");
            var cand = ctx.GetResult<string>("producer.metrics");
            var @base = ctx.GetResult<string>("baseline.merge-base");
            return Task.FromResult<object?>($"delta: {cand} vs {@base}");
        });

        builder.RegisterPolicy("policy.budget", "diffra.delta", "diffra.assessment", ["comparator.numeric"], (ctx, ct) =>
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
        builder.RegisterProducer("nodeA", "type.a", ["nodeC"], (ctx, ct) => Task.FromResult<object?>("A"));
        builder.RegisterProducer("nodeB", "type.a", ["nodeA"], (ctx, ct) => Task.FromResult<object?>("B"));
        builder.RegisterProducer("nodeC", "type.a", ["nodeB"], (ctx, ct) => Task.FromResult<object?>("C"));

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
        builder.RegisterProducer("nodeA", "type.a", ["ghost-node"], (ctx, ct) => Task.FromResult<object?>("A"));

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
        builder.RegisterProducer("nodeA", "diffra.type.a", [], (ctx, ct) => Task.FromResult<object?>("A"));
        builder.RegisterComparator("nodeB", "diffra.type.b", "diffra.delta", ["nodeA"], (ctx, ct) => Task.FromResult<object?>("B"));

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
        builder.RegisterBaseline("baselineA", "diffra.type.a", "arbitrary-role", [], (ctx, ct) => Task.FromResult<object?>("A"));

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

    private static async Task CascadingFaultIsolation()
    {
        var builder = new GraphBuilder();

        // Independent node that should succeed
        builder.RegisterSubject("independent.node", (ctx, ct) => Task.FromResult<object?>("independent-ok"));

        // Upstream failing node
        builder.RegisterProducer("failing.producer", "diffra.data", [], (ctx, ct) =>
        {
            throw new InvalidOperationException("Hardware or network glitch in producer.");
        });

        // Downstream dependent node that should be blocked
        builder.RegisterComparator("blocked.comparator", "diffra.data", "diffra.delta", ["failing.producer"], (ctx, ct) =>
        {
            return Task.FromResult<object?>("should never run");
        });

        // Downstream from blocked node that should also be blocked
        builder.RegisterPolicy("blocked.policy", "diffra.delta", "diffra.assessment", ["blocked.comparator"], (ctx, ct) =>
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
                OutputFilePath: tempFile);

            var result = await ExternalCommandAdapter.RunAsync(options).ConfigureAwait(false);

            Equal(0, result.ExitCode);
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
                runtime: Environment.Version.ToString());

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
