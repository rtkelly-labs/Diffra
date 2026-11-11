using Diffra.Graph;

namespace Diffra.Protocol.Tests;

/// <summary>Checks the graph data model and validator. The graph is data; nothing executes it.</summary>
public static class GraphTests
{
    public static void RunAll()
    {
        ValidGraphPassesValidation();
        DuplicateNodeIdentifiersAreRejected();
        GraphValidatorRejectsCycles();
        GraphValidatorRejectsMissingInputs();
        GraphValidatorRejectsIncompatibleTypes();
        GraphValidatorRejectsTypeVersionMismatch();
        GraphValidatorRejectsTypedConsumerOfUntypedNode();
        GraphValidatorRejectsImpossibleBaselineRoles();
        GraphValidatorRejectsTrustedAsBaselineRole();
        GraphValidatorRejectsAmbiguousComparators();
        Console.WriteLine("PASS graph data model and validation");
    }

    private static GraphNode Node(
        string id,
        GraphNodeKind kind,
        string? inputType = null,
        string? inputTypeVersion = null,
        string? outputType = null,
        string? outputTypeVersion = null,
        string? baselineRole = null,
        IEnumerable<string>? dependencies = null) =>
        new(id, kind, inputType, inputTypeVersion, outputType, outputTypeVersion, baselineRole, (dependencies ?? []).ToList());

    private static void ValidGraphPassesValidation()
    {
        var graph = new GraphDefinition(
        [
            Node("subject.core", GraphNodeKind.Subject, outputType: "diffra.subject", outputTypeVersion: "1.0"),
            Node("producer.metrics", GraphNodeKind.Producer, outputType: "diffra.metrics", outputTypeVersion: "1.0", dependencies: ["subject.core"]),
            Node("baseline.merge-base", GraphNodeKind.Baseline, outputType: "diffra.metrics", outputTypeVersion: "1.0", baselineRole: "merge-base", dependencies: ["subject.core"]),
            Node("comparator.numeric", GraphNodeKind.Comparator, "diffra.metrics", "1.0", "diffra.delta", "1.0", dependencies: ["producer.metrics", "baseline.merge-base"]),
            Node("policy.budget", GraphNodeKind.Policy, "diffra.delta", "1.0", "diffra.assessment", "1.0", dependencies: ["comparator.numeric"]),
        ]);

        GraphValidator.Validate(graph);
        Equal(5, graph.Nodes.Count);
    }

    private static void DuplicateNodeIdentifiersAreRejected()
    {
        try
        {
            _ = new GraphDefinition(
            [
                Node("nodeA", GraphNodeKind.Producer, outputType: "type.a", outputTypeVersion: "1.0"),
                Node("nodeA", GraphNodeKind.Producer, outputType: "type.b", outputTypeVersion: "1.0"),
            ]);
            throw new InvalidOperationException("Graph definition should have rejected a duplicate node identifier.");
        }
        catch (ArgumentException ex) when (ex.Message.Contains("Duplicate node identifier 'nodeA'", StringComparison.Ordinal))
        {
        }
    }

    private static void GraphValidatorRejectsCycles()
    {
        ExpectViolation(
            "Cycle detected",
            Node("nodeA", GraphNodeKind.Producer, outputType: "type.a", outputTypeVersion: "1.0", dependencies: ["nodeC"]),
            Node("nodeB", GraphNodeKind.Producer, outputType: "type.a", outputTypeVersion: "1.0", dependencies: ["nodeA"]),
            Node("nodeC", GraphNodeKind.Producer, outputType: "type.a", outputTypeVersion: "1.0", dependencies: ["nodeB"]));
    }

    private static void GraphValidatorRejectsMissingInputs()
    {
        ExpectViolation(
            "missing node 'ghost-node'",
            Node("nodeA", GraphNodeKind.Producer, outputType: "type.a", outputTypeVersion: "1.0", dependencies: ["ghost-node"]));
    }

    private static void GraphValidatorRejectsIncompatibleTypes()
    {
        ExpectViolation(
            "Type mismatch",
            Node("nodeA", GraphNodeKind.Producer, outputType: "diffra.type.a", outputTypeVersion: "1.0"),
            Node("nodeB", GraphNodeKind.Comparator, "diffra.type.b", "1.0", "diffra.delta", "1.0", dependencies: ["nodeA"]));
    }

    private static void GraphValidatorRejectsTypeVersionMismatch()
    {
        ExpectViolation(
            "Type version mismatch",
            Node("producer.metrics", GraphNodeKind.Producer, outputType: "org.example.metrics", outputTypeVersion: "1.0"),
            Node("comparator.metrics", GraphNodeKind.Comparator, "org.example.metrics", "2.0", "diffra.delta", "1.0", dependencies: ["producer.metrics"]));
    }

    private static void GraphValidatorRejectsTypedConsumerOfUntypedNode()
    {
        ExpectViolation(
            "upstream 'store.cas' declares no output type",
            Node("store.cas", GraphNodeKind.Store, "diffra.metrics", "1.0"),
            Node("comparator.metrics", GraphNodeKind.Comparator, "diffra.metrics", "1.0", "diffra.delta", "1.0", dependencies: ["store.cas"]));
    }

    private static void GraphValidatorRejectsImpossibleBaselineRoles()
    {
        ExpectViolation(
            "impossible role 'arbitrary-role'",
            Node("baselineA", GraphNodeKind.Baseline, outputType: "diffra.type.a", outputTypeVersion: "1.0", baselineRole: "arbitrary-role"));
    }

    private static void GraphValidatorRejectsTrustedAsBaselineRole()
    {
        // "trusted" is a baseline trust status, not a baseline role.
        ExpectViolation(
            "impossible role 'trusted'",
            Node("baselineA", GraphNodeKind.Baseline, outputType: "diffra.type.a", outputTypeVersion: "1.0", baselineRole: "trusted"));
    }

    private static void GraphValidatorRejectsAmbiguousComparators()
    {
        ExpectViolation(
            "Ambiguous comparators detected: 'comparator.a' and 'comparator.b'",
            Node("producer.metrics", GraphNodeKind.Producer, outputType: "diffra.metrics", outputTypeVersion: "1.0"),
            Node("comparator.a", GraphNodeKind.Comparator, "diffra.metrics", "1.0", "diffra.delta", "1.0", dependencies: ["producer.metrics"]),
            Node("comparator.b", GraphNodeKind.Comparator, "diffra.metrics", "1.0", "diffra.delta", "1.0", dependencies: ["producer.metrics"]));
    }

    private static void ExpectViolation(string expectedFragment, params GraphNode[] nodes)
    {
        try
        {
            GraphValidator.Validate(new GraphDefinition(nodes));
        }
        catch (GraphValidationException ex)
        {
            if (!ex.Violations.Any(v => v.Contains(expectedFragment, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Expected a violation containing '{expectedFragment}', got: {string.Join(" | ", ex.Violations)}");
            }

            return;
        }

        throw new InvalidOperationException($"Validator should have reported '{expectedFragment}'.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected: {expected}, Actual: {actual}");
        }
    }
}
