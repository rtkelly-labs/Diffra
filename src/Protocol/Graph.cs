namespace Diffra.Protocol;

/// <summary>Identifies the architectural category of a node in the Diffra composition graph.</summary>
public enum GraphNodeKind
{
    Subject,
    Producer,
    Baseline,
    Comparator,
    Policy,
    Store,
    Presenter,
}

/// <summary>Status of an individual node in the DAG execution lifecycle.</summary>
public enum NodeExecutionStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Blocked,
}

/// <summary>Context passed into each node handler containing upstream outputs and metadata.</summary>
public sealed class GraphExecutionContext
{
    private readonly Dictionary<string, NodeExecutionResult> _results = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, NodeExecutionResult> Results => _results;

    public void RecordResult(NodeExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _results[result.NodeId] = result;
    }

    public T GetResult<T>(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        if (!_results.TryGetValue(nodeId, out var res))
        {
            throw new KeyNotFoundException($"No execution result recorded for node '{nodeId}'.");
        }

        if (res.Status != NodeExecutionStatus.Completed)
        {
            throw new InvalidOperationException($"Node '{nodeId}' did not complete successfully (Status: {res.Status}, Error: {res.ErrorMessage}).");
        }

        if (res.Output is T typed)
        {
            return typed;
        }

        throw new InvalidCastException($"Node '{nodeId}' output is of type {res.Output?.GetType().FullName ?? "null"}, expected {typeof(T).FullName}.");
    }

    public bool TryGetResult<T>(string nodeId, out T? result)
    {
        result = default;
        if (_results.TryGetValue(nodeId, out var res) && res.Status == NodeExecutionStatus.Completed && res.Output is T typed)
        {
            result = typed;
            return true;
        }
        return false;
    }
}

/// <summary>Result of executing a single node within the DAG.</summary>
public sealed record NodeExecutionResult(
    string NodeId,
    NodeExecutionStatus Status,
    object? Output,
    string? ErrorMessage,
    TimeSpan Duration);

/// <summary>
/// Represents an immutable declared node in the Diffra computation graph. Typed inputs and outputs
/// carry both a type ID and a type version; V1 connections require both to match exactly.
/// </summary>
public sealed record GraphNode(
    string Id,
    GraphNodeKind Kind,
    string? InputType,
    string? InputTypeVersion,
    string? OutputType,
    string? OutputTypeVersion,
    string? BaselineRole,
    IReadOnlyList<string> Dependencies,
    Func<GraphExecutionContext, CancellationToken, Task<object?>> Handler);

/// <summary>Represents an immutable, validated graph definition.</summary>
public sealed class GraphDefinition
{
    public IReadOnlyDictionary<string, GraphNode> Nodes { get; }

    public GraphDefinition(IEnumerable<GraphNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var dict = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (dict.ContainsKey(node.Id))
            {
                throw new ArgumentException($"Duplicate node identifier '{node.Id}'.", nameof(nodes));
            }
            dict[node.Id] = node;
        }
        Nodes = dict;
    }
}

/// <summary>Fluent builder for code-first registration of Diffra composition graphs.</summary>
public sealed class GraphBuilder
{
    /// <summary>Type ID declared by every subject node's output.</summary>
    public const string SubjectTypeId = "diffra.subject";

    /// <summary>Type version declared by every subject node's output.</summary>
    public const string SubjectTypeVersion = "1.0";

    private readonly List<GraphNode> _nodes = [];

    public GraphBuilder RegisterSubject(
        string id,
        Func<GraphExecutionContext, CancellationToken, Task<object?>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(handler);
        _nodes.Add(new GraphNode(id, GraphNodeKind.Subject, null, null, SubjectTypeId, SubjectTypeVersion, null, [], handler));
        return this;
    }

    public GraphBuilder RegisterProducer(
        string id,
        string outputType,
        string outputTypeVersion,
        IEnumerable<string> dependencies,
        Func<GraphExecutionContext, CancellationToken, Task<object?>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputType);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputTypeVersion);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(handler);

        _nodes.Add(new GraphNode(id, GraphNodeKind.Producer, null, null, outputType, outputTypeVersion, null, dependencies.ToList(), handler));
        return this;
    }

    public GraphBuilder RegisterBaseline(
        string id,
        string outputType,
        string outputTypeVersion,
        string baselineRole,
        IEnumerable<string> dependencies,
        Func<GraphExecutionContext, CancellationToken, Task<object?>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputType);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputTypeVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineRole);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(handler);

        _nodes.Add(new GraphNode(id, GraphNodeKind.Baseline, null, null, outputType, outputTypeVersion, baselineRole, dependencies.ToList(), handler));
        return this;
    }

    public GraphBuilder RegisterComparator(
        string id,
        string inputType,
        string inputTypeVersion,
        string outputType,
        string outputTypeVersion,
        IEnumerable<string> dependencies,
        Func<GraphExecutionContext, CancellationToken, Task<object?>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputType);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputTypeVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputType);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputTypeVersion);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(handler);

        _nodes.Add(new GraphNode(id, GraphNodeKind.Comparator, inputType, inputTypeVersion, outputType, outputTypeVersion, null, dependencies.ToList(), handler));
        return this;
    }

    public GraphBuilder RegisterPolicy(
        string id,
        string inputType,
        string inputTypeVersion,
        string outputType,
        string outputTypeVersion,
        IEnumerable<string> dependencies,
        Func<GraphExecutionContext, CancellationToken, Task<object?>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputType);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputTypeVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputType);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputTypeVersion);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(handler);

        _nodes.Add(new GraphNode(id, GraphNodeKind.Policy, inputType, inputTypeVersion, outputType, outputTypeVersion, null, dependencies.ToList(), handler));
        return this;
    }

    public GraphBuilder RegisterStore(
        string id,
        string inputType,
        string inputTypeVersion,
        IEnumerable<string> dependencies,
        Func<GraphExecutionContext, CancellationToken, Task<object?>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputType);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputTypeVersion);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(handler);

        _nodes.Add(new GraphNode(id, GraphNodeKind.Store, inputType, inputTypeVersion, null, null, null, dependencies.ToList(), handler));
        return this;
    }

    public GraphBuilder RegisterPresenter(
        string id,
        IEnumerable<string> dependencies,
        Func<GraphExecutionContext, CancellationToken, Task<object?>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(handler);

        _nodes.Add(new GraphNode(id, GraphNodeKind.Presenter, null, null, null, null, null, dependencies.ToList(), handler));
        return this;
    }

    public GraphDefinition Build()
    {
        return new GraphDefinition(_nodes);
    }
}
