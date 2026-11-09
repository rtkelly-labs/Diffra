namespace Diffra.Protocol;

/// <summary>Exception thrown when a Diffra composition graph violates DAG invariants.</summary>
public sealed class GraphValidationException : InvalidOperationException
{
    public IReadOnlyList<string> Violations { get; }

    public GraphValidationException(IReadOnlyList<string> violations)
        : base("Graph validation failed:\n" + string.Join("\n", violations.Select(v => " - " + v)))
    {
        Violations = violations;
    }
}

/// <summary>Validates structural and type invariants of a Diffra composition graph before execution.</summary>
public static class GraphValidator
{
    private static readonly HashSet<string> AllowedBaselineRoles = new(StringComparer.Ordinal)
    {
        "merge-base",
        "approved-main",
        "previous-release",
        "trusted",
    };

    public static void Validate(GraphDefinition graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var violations = new List<string>();

        // 1. Missing inputs (unresolved dependencies)
        foreach (var (id, node) in graph.Nodes)
        {
            foreach (var dep in node.Dependencies)
            {
                if (!graph.Nodes.ContainsKey(dep))
                {
                    violations.Add($"Node '{id}' declares dependency on missing node '{dep}'.");
                }
            }
        }

        // 2. Impossible baseline roles
        foreach (var (id, node) in graph.Nodes)
        {
            if (node.Kind == GraphNodeKind.Baseline)
            {
                if (string.IsNullOrWhiteSpace(node.BaselineRole) || !AllowedBaselineRoles.Contains(node.BaselineRole))
                {
                    violations.Add($"Baseline node '{id}' declares unsupported or impossible role '{node.BaselineRole}'. Allowed roles: {string.Join(", ", AllowedBaselineRoles)}.");
                }
            }
        }

        // 3. Incompatible types between connected nodes
        foreach (var (id, node) in graph.Nodes)
        {
            if (!string.IsNullOrEmpty(node.InputType))
            {
                foreach (var dep in node.Dependencies)
                {
                    if (graph.Nodes.TryGetValue(dep, out var upstreamNode))
                    {
                        if (!string.IsNullOrEmpty(upstreamNode.OutputType) &&
                            !string.Equals(upstreamNode.OutputType, node.InputType, StringComparison.Ordinal))
                        {
                            violations.Add($"Type mismatch: Node '{id}' expects input type '{node.InputType}', but upstream dependency '{dep}' outputs '{upstreamNode.OutputType}'.");
                        }
                    }
                }
            }
        }

        // 4. Comparator ambiguity (multiple comparators claiming identical input and output without disambiguation)
        var comparators = graph.Nodes.Values.Where(n => n.Kind == GraphNodeKind.Comparator).ToList();
        for (int i = 0; i < comparators.Count; i++)
        {
            for (int j = i + 1; j < comparators.Count; j++)
            {
                var c1 = comparators[i];
                var c2 = comparators[j];
                if (string.Equals(c1.InputType, c2.InputType, StringComparison.Ordinal) &&
                    string.Equals(c1.OutputType, c2.OutputType, StringComparison.Ordinal) &&
                    c1.Dependencies.SequenceEqual(c2.Dependencies, StringComparer.Ordinal))
                {
                    violations.Add($"Ambiguous comparators detected: '{c1.Id}' and '{c2.Id}' both process input '{c1.InputType}' to produce '{c1.OutputType}' over the same dependencies.");
                }
            }
        }

        // 5. Cycle detection via DFS
        var visitState = new Dictionary<string, int>(StringComparer.Ordinal); // 0 = unvisited, 1 = visiting, 2 = visited
        var cyclePath = new List<string>();

        bool HasCycle(string current)
        {
            visitState[current] = 1;
            cyclePath.Add(current);

            if (graph.Nodes.TryGetValue(current, out var n))
            {
                foreach (var dep in n.Dependencies)
                {
                    if (!graph.Nodes.ContainsKey(dep)) continue;

                    visitState.TryGetValue(dep, out int state);
                    if (state == 1)
                    {
                        // Cycle found!
                        cyclePath.Add(dep);
                        return true;
                    }
                    if (state == 0 && HasCycle(dep))
                    {
                        return true;
                    }
                }
            }

            visitState[current] = 2;
            cyclePath.RemoveAt(cyclePath.Count - 1);
            return false;
        }

        foreach (var id in graph.Nodes.Keys)
        {
            visitState.TryGetValue(id, out int state);
            if (state == 0)
            {
                if (HasCycle(id))
                {
                    violations.Add($"Cycle detected in graph dependencies: {string.Join(" -> ", cyclePath)}.");
                    break;
                }
            }
        }

        if (violations.Count > 0)
        {
            throw new GraphValidationException(violations);
        }
    }
}
