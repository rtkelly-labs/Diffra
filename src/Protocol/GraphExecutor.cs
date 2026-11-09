using System.Diagnostics;

namespace Diffra.Protocol;

/// <summary>Summary report emitted following execution of a Diffra composition graph.</summary>
public sealed record GraphExecutionReport(
    IReadOnlyDictionary<string, NodeExecutionResult> Results,
    bool Success,
    TimeSpan TotalDuration)
{
    public NodeExecutionResult GetResult(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        return Results[nodeId];
    }
}

/// <summary>Executes a validated Diffra DAG in deterministic topological order with cascading fault isolation.</summary>
public static class GraphExecutor
{
    public static async Task<GraphExecutionReport> ExecuteAsync(
        GraphDefinition graph,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);

        // 1. Validate graph structure, cycles, types, and roles before any execution
        GraphValidator.Validate(graph);

        var stopwatch = Stopwatch.StartNew();
        var context = new GraphExecutionContext();
        var executionOrder = GetTopologicalOrder(graph);

        foreach (var node in executionOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 2. Check upstream dependencies
            string? blockingDep = null;
            foreach (var dep in node.Dependencies)
            {
                if (context.Results.TryGetValue(dep, out var depResult))
                {
                    if (depResult.Status != NodeExecutionStatus.Completed)
                    {
                        blockingDep = dep;
                        break;
                    }
                }
            }

            if (blockingDep != null)
            {
                context.RecordResult(new NodeExecutionResult(
                    NodeId: node.Id,
                    Status: NodeExecutionStatus.Blocked,
                    Output: null,
                    ErrorMessage: $"Blocked: Upstream dependency '{blockingDep}' did not complete successfully.",
                    Duration: TimeSpan.Zero));
                continue;
            }

            // 3. Execute node handler
            var nodeSw = Stopwatch.StartNew();
            try
            {
                var output = await node.Handler(context, cancellationToken).ConfigureAwait(false);
                nodeSw.Stop();

                context.RecordResult(new NodeExecutionResult(
                    NodeId: node.Id,
                    Status: NodeExecutionStatus.Completed,
                    Output: output,
                    ErrorMessage: null,
                    Duration: nodeSw.Elapsed));
            }
            catch (Exception ex)
            {
                nodeSw.Stop();
                context.RecordResult(new NodeExecutionResult(
                    NodeId: node.Id,
                    Status: NodeExecutionStatus.Failed,
                    Output: null,
                    ErrorMessage: ex.Message,
                    Duration: nodeSw.Elapsed));
            }
        }

        stopwatch.Stop();
        bool allCompleted = context.Results.Values.All(r => r.Status == NodeExecutionStatus.Completed);

        return new GraphExecutionReport(context.Results, allCompleted, stopwatch.Elapsed);
    }

    /// <summary>Calculates a stable topological ordering of the graph using Kahn's algorithm.</summary>
    public static IReadOnlyList<GraphNode> GetTopologicalOrder(GraphDefinition graph)
    {
        // Build in-degree map and adjacency list (upstream -> downstream)
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var downstream = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var id in graph.Nodes.Keys)
        {
            inDegree[id] = 0;
            downstream[id] = [];
        }

        foreach (var (id, node) in graph.Nodes)
        {
            inDegree[id] = node.Dependencies.Count;
            foreach (var dep in node.Dependencies)
            {
                if (downstream.TryGetValue(dep, out var list))
                {
                    list.Add(id);
                }
            }
        }

        // Deterministic queue using sorted order for identical in-degrees
        var ready = new SortedSet<string>(graph.Nodes.Keys.Where(k => inDegree[k] == 0), StringComparer.Ordinal);
        var result = new List<GraphNode>(graph.Nodes.Count);

        while (ready.Count > 0)
        {
            var current = ready.Min!;
            ready.Remove(current);
            result.Add(graph.Nodes[current]);

            foreach (var child in downstream[current])
            {
                inDegree[child]--;
                if (inDegree[child] == 0)
                {
                    ready.Add(child);
                }
            }
        }

        return result;
    }
}
