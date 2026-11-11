namespace Diffra.Graph;

/// <summary>Identifies the architectural category of a node in a Diffra graph.</summary>
public enum GraphNodeKind
{
    Subject,
    Producer,
    Baseline,
    Comparator,
    Policy,
    Store,
}

/// <summary>
/// A declared node in a Diffra graph. Typed inputs and outputs carry both a type ID and a type
/// version; connections require both to match exactly. The graph is data: nothing executes it.
/// </summary>
public sealed record GraphNode(
    string Id,
    GraphNodeKind Kind,
    string? InputType,
    string? InputTypeVersion,
    string? OutputType,
    string? OutputTypeVersion,
    string? BaselineRole,
    IReadOnlyList<string> Dependencies);

/// <summary>An immutable set of graph nodes keyed by identifier.</summary>
public sealed class GraphDefinition
{
    public IReadOnlyDictionary<string, GraphNode> Nodes { get; }

    public GraphDefinition(IEnumerable<GraphNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var dict = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!dict.TryAdd(node.Id, node))
            {
                throw new ArgumentException($"Duplicate node identifier '{node.Id}'.", nameof(nodes));
            }
        }
        Nodes = dict;
    }
}
