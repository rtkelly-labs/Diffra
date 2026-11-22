using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Diffra.Protocol;

/// <summary>Identifies the tool and semantic fidelity that produced an architecture graph.</summary>
public sealed record ArchAnalyzer(
    string Id,
    string Tool,
    string? ToolVersion,
    string Language,
    string Resolution);

/// <summary>One directed reference backing a module dependency edge, using SCIP vocabulary.</summary>
public sealed record ArchEdgeEvidence(
    string RelativePath,
    int? Line,
    string? SourceSymbol,
    string? TargetSymbol,
    IReadOnlyList<string> Roles,
    string Relationship)
{
    /// <summary>Compact single-line citation suitable for Diffra datasets and Markdown reports.</summary>
    public string FormatCompact()
    {
        var symbols = (SourceSymbol, TargetSymbol) switch
        {
            ({ Length: > 0 } src, { Length: > 0 } dst) => $"{src}->{dst}",
            (_, { Length: > 0 } dst) => dst,
            ({ Length: > 0 } src, _) => src,
            _ => Relationship,
        };
        var location = Line is { } lineNumber ? $"{RelativePath}:{lineNumber.ToString(CultureInfo.InvariantCulture)}" : RelativePath;
        return $"{symbols}@{location}";
    }
}

/// <summary>One module, namespace, package, crate, assembly or file node in an architecture graph.</summary>
public sealed record ArchNode(
    string Id,
    string Label,
    string Kind,
    string Language,
    string? Project,
    string? Layer,
    string? Slice,
    string? Path,
    int FileCount,
    int SymbolCount,
    int FanIn,
    int FanOut,
    string Instability);

/// <summary>One directed dependency edge between two canonical nodes.</summary>
public sealed record ArchEdge(
    string Id,
    string Source,
    string Target,
    string Relation,
    int Weight,
    string LayerRelation,
    bool SameFeatureSlice,
    bool Cyclic,
    bool TypeOnly,
    IReadOnlyList<ArchEdgeEvidence> Evidence,
    bool EvidenceTruncated);

/// <summary>One strongly connected component (cycle) identified by deterministic Tarjan condensation.</summary>
public sealed record ArchCycle(
    string Id,
    IReadOnlyList<string> Members,
    IReadOnlyList<string> EdgeIds);

/// <summary>Exact scalar summary metrics for an architecture graph, preserved even under truncation.</summary>
public sealed record ArchGraphSummary(
    int NodeCount,
    int EdgeCount,
    int CycleCount,
    int FeatureCrossEdgeCount,
    int LayerViolationCount,
    int UnassignedNodeCount);

/// <summary>Normalized, deterministic JGF v2.1 + SCIP-vocabulary architecture graph.</summary>
public sealed record ArchGraphDocument(
    string Id,
    string Label,
    string? Repo,
    string? Commit,
    ArchAnalyzer Analyzer,
    string Granularity,
    IReadOnlyList<string> Layers,
    IReadOnlyDictionary<string, ArchNode> Nodes,
    IReadOnlyList<ArchEdge> Edges,
    IReadOnlyDictionary<string, ArchCycle> Cycles,
    ArchGraphSummary Summary,
    IReadOnlyList<string> Warnings);

/// <summary>Raw node input before derived metrics (fan-in, fan-out, instability) are computed.</summary>
public sealed record RawArchNode(
    string Id,
    string Label,
    string Kind,
    string Language,
    string? Project = null,
    string? Layer = null,
    string? Slice = null,
    string? Path = null,
    int FileCount = 0,
    int SymbolCount = 0);

/// <summary>Raw directed edge input before condensation, layer classification and evidence capping.</summary>
public sealed record RawArchEdge(
    string Source,
    string Target,
    string Relation,
    int Weight,
    bool TypeOnly,
    IReadOnlyList<ArchEdgeEvidence> Evidence);

/// <summary>
/// Builds canonical <see cref="ArchGraphDocument"/> instances: computes deterministic Tarjan SCCs,
/// layer relations, feature-to-feature crossings, fan-in/fan-out, and Robert Martin instability.
/// </summary>
public static class ArchGraphBuilder
{
    public const string DatasetType = "diffra/arch-graph@1";
    public const string GraphSchema = "https://diffra.dev/schemas/arch-graph-v1.schema.json";
    public const int DefaultMaxEvidencePerEdge = 20;

    private static readonly HashSet<string> ValidResolutions = new(
        ["scip-symbol", "il-symbol", "hir-symbol", "compiler-package", "compiler-module", "import-ast"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> ValidKinds = new(
        ["namespace", "module", "package", "crate", "assembly", "file"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> ValidRelations = new(
        ["symbol", "import", "type-only", "test-import", "project-ref"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> ValidRoles = new(
        ["Definition", "Import", "WriteAccess", "ReadAccess", "Generated", "Test", "ForwardDefinition"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> ValidRelationships = new(
        ["reference", "implementation", "type_definition", "definition", "import"],
        StringComparer.Ordinal);

    /// <summary>Canonical edge key: <c>&lt;source&gt;-&gt;&lt;target&gt;</c>.</summary>
    public static string EdgeId(string source, string target) => $"{source}->{target}";

    /// <summary>Canonical cycle ID: <c>cycle:</c> plus first 12 lowercase hex chars of SHA-256 over sorted members joined by <c>|</c>.</summary>
    public static string CycleId(IEnumerable<string> members)
    {
        var sorted = members.Order(StringComparer.Ordinal);
        return "cycle:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', sorted))))[..12];
    }

    /// <summary>Computes Robert Martin instability <c>fanOut / (fanIn + fanOut)</c> as a canonical decimal string (half-to-even, up to 6 decimals).</summary>
    public static string FormatInstability(int fanIn, int fanOut)
    {
        var total = fanIn + fanOut;
        if (total <= 0)
        {
            return "0";
        }

        var value = Math.Round((decimal)fanOut / total, 6, MidpointRounding.ToEven);
        return CanonicalDecimal(value);
    }

    /// <summary>Formats a decimal without trailing fractional zeros or negative zero.</summary>
    public static string CanonicalDecimal(decimal value)
    {
        if (value == 0m)
        {
            return "0";
        }

        var text = value.ToString("0.######", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    /// <summary>Builds a fully normalized, deterministic <see cref="ArchGraphDocument"/> from raw nodes and edges.</summary>
    public static ArchGraphDocument Build(
        string id,
        string label,
        string? repo,
        string? commit,
        ArchAnalyzer analyzer,
        string granularity,
        IReadOnlyList<string>? layers,
        IEnumerable<RawArchNode> rawNodes,
        IEnumerable<RawArchEdge> rawEdges,
        IEnumerable<string>? warnings = null,
        int maxEvidencePerEdge = DefaultMaxEvidencePerEdge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(rawNodes);
        ArgumentNullException.ThrowIfNull(rawEdges);
        if (!ValidResolutions.Contains(analyzer.Resolution))
        {
            throw new JsonException($"Unsupported analyzer resolution '{analyzer.Resolution}'.");
        }

        if (!ValidKinds.Contains(granularity))
        {
            throw new JsonException($"Unsupported graph granularity '{granularity}'.");
        }

        var orderedLayers = (layers ?? [])
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var layerIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < orderedLayers.Length; i++)
        {
            layerIndex[orderedLayers[i]] = i;
        }

        // Deduplicate and sort nodes by ordinal ID.
        var rawNodeMap = new SortedDictionary<string, RawArchNode>(StringComparer.Ordinal);
        foreach (var node in rawNodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                throw new JsonException("Architecture graph node ID cannot be empty.");
            }

            if (!ValidKinds.Contains(node.Kind))
            {
                throw new JsonException($"Unsupported node kind '{node.Kind}' on '{node.Id}'.");
            }

            if (node.Path is not null && !IsSafeRelativePath(node.Path))
            {
                throw new JsonException($"Unsafe node path '{node.Path}' on '{node.Id}'.");
            }

            rawNodeMap[node.Id] = node;
        }

        // Merge parallel edges between the same (source, target), dropping self-edges and ensuring endpoint nodes exist.
        var merged = new SortedDictionary<string, EdgeAccumulator>(StringComparer.Ordinal);
        foreach (var edge in rawEdges)
        {
            if (string.IsNullOrWhiteSpace(edge.Source) || string.IsNullOrWhiteSpace(edge.Target))
            {
                throw new JsonException("Edge source and target cannot be empty.");
            }

            if (!ValidRelations.Contains(edge.Relation))
            {
                throw new JsonException($"Unsupported edge relation '{edge.Relation}'.");
            }

            if (string.Equals(edge.Source, edge.Target, StringComparison.Ordinal))
            {
                continue;
            }

            if (!rawNodeMap.ContainsKey(edge.Source))
            {
                rawNodeMap[edge.Source] = new RawArchNode(edge.Source, ShortLabel(edge.Source), granularity, analyzer.Language);
            }

            if (!rawNodeMap.ContainsKey(edge.Target))
            {
                rawNodeMap[edge.Target] = new RawArchNode(edge.Target, ShortLabel(edge.Target), granularity, analyzer.Language);
            }

            var key = EdgeId(edge.Source, edge.Target);
            if (!merged.TryGetValue(key, out var acc))
            {
                acc = new EdgeAccumulator(edge.Source, edge.Target, edge.Relation, edge.TypeOnly);
                merged[key] = acc;
            }
            else
            {
                acc.MergeRelation(edge.Relation, edge.TypeOnly);
            }

            acc.AddWeight(Math.Max(1, edge.Weight));
            foreach (var ev in edge.Evidence)
            {
                ValidateEvidence(ev);
                acc.AddEvidence(ev);
            }
        }

        // Compute deterministic Tarjan SCCs over ordinal-sorted adjacency lists.
        var adjacency = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var nodeId in rawNodeMap.Keys)
        {
            adjacency[nodeId] = new SortedSet<string>(StringComparer.Ordinal);
        }

        foreach (var acc in merged.Values)
        {
            adjacency[acc.Source].Add(acc.Target);
        }

        var sccs = ComputeTarjanSccs(adjacency);
        var cycles = new SortedDictionary<string, ArchCycle>(StringComparer.Ordinal);
        var cyclicEdgeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var members in sccs)
        {
            if (members.Count < 2)
            {
                continue;
            }

            var memberSet = new HashSet<string>(members, StringComparer.Ordinal);
            var edgeIds = merged.Values
                .Where(e => memberSet.Contains(e.Source) && memberSet.Contains(e.Target))
                .Select(e => EdgeId(e.Source, e.Target))
                .Order(StringComparer.Ordinal)
                .ToArray();
            foreach (var edgeId in edgeIds)
            {
                cyclicEdgeIds.Add(edgeId);
            }

            var cycleId = CycleId(members);
            cycles[cycleId] = new ArchCycle(cycleId, members, edgeIds);
        }

        // Compute fan-in / fan-out per node.
        var fanIn = rawNodeMap.Keys.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);
        var fanOut = rawNodeMap.Keys.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);
        foreach (var acc in merged.Values)
        {
            fanOut[acc.Source]++;
            fanIn[acc.Target]++;
        }

        var nodes = new SortedDictionary<string, ArchNode>(StringComparer.Ordinal);
        var unassignedCount = 0;
        foreach (var (nodeId, raw) in rawNodeMap)
        {
            var layer = string.IsNullOrWhiteSpace(raw.Layer) ? null : raw.Layer;
            if (orderedLayers.Length > 0 && (layer is null || !layerIndex.ContainsKey(layer)))
            {
                unassignedCount++;
            }

            var incoming = fanIn[nodeId];
            var outgoing = fanOut[nodeId];
            nodes[nodeId] = new ArchNode(
                Id: nodeId,
                Label: string.IsNullOrWhiteSpace(raw.Label) ? ShortLabel(nodeId) : raw.Label,
                Kind: raw.Kind,
                Language: raw.Language,
                Project: raw.Project,
                Layer: layer,
                Slice: string.IsNullOrWhiteSpace(raw.Slice) ? null : raw.Slice,
                Path: raw.Path,
                FileCount: Math.Max(0, raw.FileCount),
                SymbolCount: Math.Max(0, raw.SymbolCount),
                FanIn: incoming,
                FanOut: outgoing,
                Instability: FormatInstability(incoming, outgoing));
        }

        // Finalize edges with layer relations, feature-to-feature crossings, and capped sorted evidence.
        var edges = new List<ArchEdge>(merged.Count);
        var layerViolationCount = 0;
        var featureCrossEdgeCount = 0;
        foreach (var (edgeId, acc) in merged)
        {
            var sourceNode = nodes[acc.Source];
            var targetNode = nodes[acc.Target];
            var layerRelation = ClassifyLayerRelation(sourceNode.Layer, targetNode.Layer, layerIndex);
            if (layerRelation == "up")
            {
                layerViolationCount++;
            }

            var sameFeatureSlice = IsSameFeatureSlice(sourceNode, targetNode);
            if (IsFeatureCrossEdge(sourceNode, targetNode))
            {
                featureCrossEdgeCount++;
            }

            var (boundedEvidence, truncated) = acc.FinalizeEvidence(maxEvidencePerEdge);
            edges.Add(new ArchEdge(
                Id: edgeId,
                Source: acc.Source,
                Target: acc.Target,
                Relation: acc.Relation,
                Weight: acc.Weight,
                LayerRelation: layerRelation,
                SameFeatureSlice: sameFeatureSlice,
                Cyclic: cyclicEdgeIds.Contains(edgeId),
                TypeOnly: acc.TypeOnly,
                Evidence: boundedEvidence,
                EvidenceTruncated: truncated));
        }

        var summary = new ArchGraphSummary(
            NodeCount: nodes.Count,
            EdgeCount: edges.Count,
            CycleCount: cycles.Count,
            FeatureCrossEdgeCount: featureCrossEdgeCount,
            LayerViolationCount: layerViolationCount,
            UnassignedNodeCount: unassignedCount);

        var sortedWarnings = (warnings ?? [])
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new ArchGraphDocument(
            Id: id,
            Label: string.IsNullOrWhiteSpace(label) ? id : label,
            Repo: string.IsNullOrWhiteSpace(repo) ? null : repo,
            Commit: string.IsNullOrWhiteSpace(commit) ? null : commit,
            Analyzer: analyzer,
            Granularity: granularity,
            Layers: orderedLayers,
            Nodes: nodes,
            Edges: edges,
            Cycles: cycles,
            Summary: summary,
            Warnings: sortedWarnings);
    }

    /// <summary>Validates that a path is repository-relative POSIX with no traversal, backslashes or control characters.</summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1000)
        {
            return false;
        }

        if (path[0] == '/' || path[0] == '~' || path.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            return false;
        }

        foreach (var rune in path.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
            {
                return false;
            }
        }

        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateEvidence(ArchEdgeEvidence evidence)
    {
        if (!IsSafeRelativePath(evidence.RelativePath))
        {
            throw new JsonException($"Unsafe evidence path '{evidence.RelativePath}'.");
        }

        if (evidence.Line is < 1)
        {
            throw new JsonException($"Evidence line must be >= 1, got {evidence.Line}.");
        }

        if (!ValidRelationships.Contains(evidence.Relationship))
        {
            throw new JsonException($"Unsupported evidence relationship '{evidence.Relationship}'.");
        }

        foreach (var role in evidence.Roles)
        {
            if (!ValidRoles.Contains(role))
            {
                throw new JsonException($"Unsupported SCIP symbol role '{role}'.");
            }
        }
    }

    private static string ClassifyLayerRelation(string? sourceLayer, string? targetLayer, Dictionary<string, int> layerIndex)
    {
        if (sourceLayer is null || targetLayer is null
            || !layerIndex.TryGetValue(sourceLayer, out var sourceIdx)
            || !layerIndex.TryGetValue(targetLayer, out var targetIdx))
        {
            return "unassigned";
        }

        if (targetIdx > sourceIdx)
        {
            return "down";
        }

        return targetIdx == sourceIdx ? "same" : "up";
    }

    private static bool IsSameFeatureSlice(ArchNode source, ArchNode target) =>
        source.Slice is not null
        && target.Slice is not null
        && string.Equals(source.Slice, target.Slice, StringComparison.Ordinal);

    private static bool IsFeatureCrossEdge(ArchNode source, ArchNode target) =>
        string.Equals(source.Layer, "features", StringComparison.Ordinal)
        && string.Equals(target.Layer, "features", StringComparison.Ordinal)
        && source.Slice is not null
        && target.Slice is not null
        && !string.Equals(source.Slice, target.Slice, StringComparison.Ordinal);

    private static string ShortLabel(string id)
    {
        var lastDot = id.LastIndexOf('.');
        var lastSlash = id.LastIndexOf('/');
        var lastColon = id.LastIndexOf("::", StringComparison.Ordinal);
        var cut = Math.Max(lastDot, lastSlash);
        if (lastColon >= 0 && lastColon + 1 > cut)
        {
            return id[(lastColon + 2)..];
        }

        return cut >= 0 && cut + 1 < id.Length ? id[(cut + 1)..] : id;
    }

    private static List<IReadOnlyList<string>> ComputeTarjanSccs(Dictionary<string, SortedSet<string>> adjacency)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var result = new List<IReadOnlyList<string>>();
        var counter = 0;

        void Visit(string node)
        {
            index[node] = counter;
            low[node] = counter;
            counter++;
            stack.Push(node);
            active.Add(node);

            foreach (var next in adjacency[node])
            {
                if (!index.TryGetValue(next, out var nextIndex))
                {
                    Visit(next);
                    low[node] = Math.Min(low[node], low[next]);
                }
                else if (active.Contains(next))
                {
                    low[node] = Math.Min(low[node], nextIndex);
                }
            }

            if (low[node] != index[node])
            {
                return;
            }

            var members = new List<string>();
            while (stack.TryPop(out var popped))
            {
                active.Remove(popped);
                members.Add(popped);
                if (string.Equals(popped, node, StringComparison.Ordinal))
                {
                    break;
                }
            }

            members.Sort(StringComparer.Ordinal);
            result.Add(members);
        }

        foreach (var node in adjacency.Keys.Order(StringComparer.Ordinal))
        {
            if (!index.ContainsKey(node))
            {
                Visit(node);
            }
        }

        return result;
    }

    private sealed class EdgeAccumulator(string source, string target, string relation, bool typeOnly)
    {
        public string Source { get; } = source;
        public string Target { get; } = target;
        public string Relation { get; private set; } = relation;
        public bool TypeOnly { get; private set; } = typeOnly;
        public int Weight { get; private set; }
        private readonly List<ArchEdgeEvidence> _evidence = [];

        public void MergeRelation(string otherRelation, bool otherTypeOnly)
        {
            if (!otherTypeOnly)
            {
                TypeOnly = false;
            }

            if (RelationPriority(otherRelation) > RelationPriority(Relation))
            {
                Relation = otherRelation;
            }
        }

        public void AddWeight(int amount) => Weight += amount;

        public void AddEvidence(ArchEdgeEvidence item) => _evidence.Add(item);

        public (IReadOnlyList<ArchEdgeEvidence> Items, bool Truncated) FinalizeEvidence(int cap)
        {
            var ordered = _evidence
                .Select(e => e with { Roles = e.Roles.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() })
                .DistinctBy(e => (e.RelativePath, e.Line, e.SourceSymbol, e.TargetSymbol, e.Relationship, string.Join(',', e.Roles)))
                .OrderBy(e => e.RelativePath, StringComparer.Ordinal)
                .ThenBy(e => e.Line ?? int.MaxValue)
                .ThenBy(e => e.SourceSymbol ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(e => e.TargetSymbol ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(e => e.Relationship, StringComparer.Ordinal)
                .ToList();

            if (ordered.Count <= cap)
            {
                return (ordered, false);
            }

            return (ordered.Take(cap).ToArray(), true);
        }

        private static int RelationPriority(string relation) => relation switch
        {
            "symbol" => 5,
            "import" => 4,
            "project-ref" => 3,
            "type-only" => 2,
            "test-import" => 1,
            _ => 0,
        };
    }
}

/// <summary>Serializes and parses canonical JGF v2.1 architecture graphs and Diffra <c>diffra/arch-graph@1</c> dataset entries.</summary>
public static class ArchGraphJson
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Serializes a single graph into canonical JGF v2.1 (<c>{ "graph": ... }</c>) UTF-8 bytes.</summary>
    public static byte[] SerializeJgf(ArchGraphDocument graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("graph");
            WriteArchGraph(writer, graph);
            writer.WriteEndObject();
        }

        return CanonicalJson.Canonicalize(stream.ToArray());
    }

    /// <summary>Serializes a multi-target graph bundle into canonical JGF v2.1 (<c>{ "graphs": [...] }</c>).</summary>
    public static byte[] SerializeJgfBundle(IReadOnlyList<ArchGraphDocument> graphs)
    {
        ArgumentNullException.ThrowIfNull(graphs);
        if (graphs.Count == 0)
        {
            throw new ArgumentException("At least one graph is required.", nameof(graphs));
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("graphs");
            foreach (var graph in graphs.OrderBy(g => g.Id, StringComparer.Ordinal))
            {
                WriteArchGraph(writer, graph);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return CanonicalJson.Canonicalize(stream.ToArray());
    }

    /// <summary>
    /// Serializes an <see cref="ArchGraphDocument"/> into Diffra's <c>diffra/arch-graph@1</c> entry <c>data</c> shape
    /// (keyed <c>nodes</c>, <c>edges</c>, <c>cycles</c> + exact decimal-string summary counts), applying
    /// priority <paramref name="maxKeys"/> truncation when set.
    /// </summary>
    public static (string DataJson, int DroppedNodes, int DroppedEdges) SerializeDatasetData(
        ArchGraphDocument graph,
        int? maxKeys = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var keptEdges = graph.Edges.ToList();
        var droppedEdges = 0;
        if (maxKeys is { } edgeBudget && keptEdges.Count > edgeBudget)
        {
            keptEdges = keptEdges
                .OrderByDescending(EdgeRetentionPriority)
                .ThenByDescending(e => e.Weight)
                .ThenBy(e => e.Id, StringComparer.Ordinal)
                .Take(edgeBudget)
                .OrderBy(e => e.Id, StringComparer.Ordinal)
                .ToList();
            droppedEdges = graph.Edges.Count - keptEdges.Count;
        }

        var keptNodes = graph.Nodes.Values.ToList();
        var droppedNodes = 0;
        if (maxKeys is { } nodeBudget && keptNodes.Count > nodeBudget)
        {
            var cyclicMembers = new HashSet<string>(graph.Cycles.Values.SelectMany(c => c.Members), StringComparer.Ordinal);
            keptNodes = keptNodes
                .OrderByDescending(n => cyclicMembers.Contains(n.Id) ? 1 : 0)
                .ThenByDescending(n => n.FanIn + n.FanOut)
                .ThenBy(n => n.Id, StringComparer.Ordinal)
                .Take(nodeBudget)
                .OrderBy(n => n.Id, StringComparer.Ordinal)
                .ToList();
            droppedNodes = graph.Nodes.Count - keptNodes.Count;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();

            writer.WriteStartObject("cycleCount");
            writer.WriteString("value", graph.Summary.CycleCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();

            writer.WriteStartObject("cycles");
            foreach (var (cycleId, cycle) in graph.Cycles)
            {
                writer.WriteStartObject(cycleId);
                writer.WriteStartObject("value");
                writer.WriteString("edgeCount", cycle.EdgeIds.Count.ToString(CultureInfo.InvariantCulture));
                writer.WriteStartArray("members");
                foreach (var member in cycle.Members)
                {
                    writer.WriteStringValue(member);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();

            writer.WriteStartObject("edgeCount");
            writer.WriteString("value", graph.Summary.EdgeCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();

            writer.WriteStartObject("edges");
            foreach (var edge in keptEdges)
            {
                writer.WriteStartObject(edge.Id);
                writer.WriteStartObject("value");
                writer.WriteBoolean("cyclic", edge.Cyclic);
                writer.WriteString("from", edge.Source);
                writer.WriteString("layerRelation", edge.LayerRelation);
                writer.WriteString("relation", edge.Relation);
                writer.WriteBoolean("sameFeatureSlice", edge.SameFeatureSlice);
                writer.WriteStartArray("sampleEvidence");
                foreach (var ev in edge.Evidence.Take(3))
                {
                    writer.WriteStringValue(ev.FormatCompact());
                }

                writer.WriteEndArray();
                writer.WriteString("to", edge.Target);
                writer.WriteBoolean("typeOnly", edge.TypeOnly);
                writer.WriteString("weight", edge.Weight.ToString(CultureInfo.InvariantCulture));
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();

            writer.WriteStartObject("featureCrossEdgeCount");
            writer.WriteString("value", graph.Summary.FeatureCrossEdgeCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();

            writer.WriteStartObject("layerViolationCount");
            writer.WriteString("value", graph.Summary.LayerViolationCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();

            writer.WriteStartObject("nodeCount");
            writer.WriteString("value", graph.Summary.NodeCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();

            writer.WriteStartObject("nodes");
            foreach (var node in keptNodes)
            {
                writer.WriteStartObject(node.Id);
                writer.WriteStartObject("value");
                writer.WriteString("fanIn", node.FanIn.ToString(CultureInfo.InvariantCulture));
                writer.WriteString("fanOut", node.FanOut.ToString(CultureInfo.InvariantCulture));
                writer.WriteString("fileCount", node.FileCount.ToString(CultureInfo.InvariantCulture));
                writer.WriteString("instability", node.Instability);
                writer.WriteString("kind", node.Kind);
                if (node.Layer is not null)
                {
                    writer.WriteString("layer", node.Layer);
                }

                if (node.Path is not null)
                {
                    writer.WriteString("path", node.Path);
                }

                if (node.Project is not null)
                {
                    writer.WriteString("project", node.Project);
                }

                if (node.Slice is not null)
                {
                    writer.WriteString("slice", node.Slice);
                }

                writer.WriteString("symbolCount", node.SymbolCount.ToString(CultureInfo.InvariantCulture));
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();

            writer.WriteStartObject("unassignedNodeCount");
            writer.WriteString("value", graph.Summary.UnassignedNodeCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return (Encoding.UTF8.GetString(CanonicalJson.Canonicalize(stream.ToArray())), droppedNodes, droppedEdges);
    }

    /// <summary>Parses and normalizes a JGF v2.1 architecture graph (<c>{ "graph": ... }</c> or first target in <c>{ "graphs": [...] }</c>).</summary>
    public static ArchGraphDocument ParseJgf(ReadOnlySpan<byte> utf8Json, string? targetId = null, IReadOnlyList<string>? overrideLayers = null)
    {
        var graphs = ParseJgfAll(utf8Json, overrideLayers);
        if (targetId is null)
        {
            return graphs[0];
        }

        return graphs.FirstOrDefault(g => string.Equals(g.Id, targetId, StringComparison.Ordinal))
            ?? throw new JsonException($"Architecture graph target '{targetId}' was not found.");
    }

    /// <summary>Parses all graphs from a JGF v2.1 architecture graph JSON document.</summary>
    public static IReadOnlyList<ArchGraphDocument> ParseJgfAll(ReadOnlySpan<byte> utf8Json, IReadOnlyList<string>? overrideLayers = null)
    {
        using var document = CanonicalJson.Parse(utf8Json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("JGF architecture graph must be a JSON object.");
        }

        var hasGraph = root.TryGetProperty("graph", out var single);
        var hasGraphs = root.TryGetProperty("graphs", out var multiple);
        if (hasGraph == hasGraphs)
        {
            throw new JsonException("JGF architecture graph must contain exactly one of 'graph' or 'graphs'.");
        }

        if (hasGraph)
        {
            RequireOnlyProperties(root, "graph");
            return [ParseSingleGraph(single, overrideLayers)];
        }

        RequireOnlyProperties(root, "graphs");
        if (multiple.ValueKind != JsonValueKind.Array || multiple.GetArrayLength() == 0)
        {
            throw new JsonException("JGF 'graphs' must be a non-empty array.");
        }

        var list = new List<ArchGraphDocument>(multiple.GetArrayLength());
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in multiple.EnumerateArray())
        {
            var parsed = ParseSingleGraph(item, overrideLayers);
            if (!seenIds.Add(parsed.Id))
            {
                throw new JsonException($"Duplicate graph id '{parsed.Id}'.");
            }

            list.Add(parsed);
        }

        return list;
    }

    private static ArchGraphDocument ParseSingleGraph(JsonElement element, IReadOnlyList<string>? overrideLayers)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("JGF graph entry must be an object.");
        }

        RequireOnlyProperties(element, "id", "label", "directed", "type", "metadata", "nodes", "edges");
        var id = RequireString(element, "id");
        var label = OptionalString(element, "label") ?? id;
        if (!element.TryGetProperty("directed", out var directed) || directed.ValueKind != JsonValueKind.True)
        {
            throw new JsonException("Architecture graph must set 'directed' to true.");
        }

        if (!element.TryGetProperty("type", out var typeProp) || typeProp.GetString() != ArchGraphBuilder.DatasetType)
        {
            throw new JsonException($"Architecture graph 'type' must be '{ArchGraphBuilder.DatasetType}'.");
        }

        var metadata = RequireObject(element, "metadata");
        RequireOnlyProperties(metadata, "repo", "commit", "analyzer", "granularity", "layers", "cycles", "summary", "warnings");
        var repo = OptionalString(metadata, "repo");
        var commit = OptionalString(metadata, "commit");
        var granularity = RequireString(metadata, "granularity");
        var analyzerElement = RequireObject(metadata, "analyzer");
        RequireOnlyProperties(analyzerElement, "id", "tool", "toolVersion", "language", "resolution");
        var analyzer = new ArchAnalyzer(
            RequireString(analyzerElement, "id"),
            RequireString(analyzerElement, "tool"),
            OptionalString(analyzerElement, "toolVersion"),
            RequireString(analyzerElement, "language"),
            RequireString(analyzerElement, "resolution"));

        var layers = overrideLayers ?? ReadOptionalStringArray(metadata, "layers");
        var warnings = ReadOptionalStringArray(metadata, "warnings");

        var nodesElement = RequireObject(element, "nodes");
        var rawNodes = new List<RawArchNode>();
        foreach (var prop in nodesElement.EnumerateObject())
        {
            var nodeObj = prop.Value;
            if (nodeObj.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException($"Node '{prop.Name}' must be an object.");
            }

            RequireOnlyProperties(nodeObj, "label", "metadata");
            var nodeLabel = RequireString(nodeObj, "label");
            var nodeMeta = RequireObject(nodeObj, "metadata");
            RequireOnlyProperties(nodeMeta, "kind", "language", "project", "layer", "slice", "path", "fileCount", "symbolCount", "fanIn", "fanOut", "instability");
            rawNodes.Add(new RawArchNode(
                Id: prop.Name,
                Label: nodeLabel,
                Kind: RequireString(nodeMeta, "kind"),
                Language: RequireString(nodeMeta, "language"),
                Project: OptionalString(nodeMeta, "project"),
                Layer: OptionalString(nodeMeta, "layer"),
                Slice: OptionalString(nodeMeta, "slice"),
                Path: OptionalString(nodeMeta, "path"),
                FileCount: OptionalNonNegativeInt(nodeMeta, "fileCount"),
                SymbolCount: OptionalNonNegativeInt(nodeMeta, "symbolCount")));
        }

        var edgesElement = RequireArray(element, "edges");
        var rawEdges = new List<RawArchEdge>();
        foreach (var edgeObj in edgesElement.EnumerateArray())
        {
            if (edgeObj.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Each edge must be an object.");
            }

            RequireOnlyProperties(edgeObj, "id", "source", "target", "relation", "directed", "label", "metadata");
            var source = RequireString(edgeObj, "source");
            var target = RequireString(edgeObj, "target");
            var relation = RequireString(edgeObj, "relation");
            if (!edgeObj.TryGetProperty("directed", out var edgeDirected) || edgeDirected.ValueKind != JsonValueKind.True)
            {
                throw new JsonException($"Edge '{source}->{target}' must set 'directed' to true.");
            }

            var edgeMeta = RequireObject(edgeObj, "metadata");
            RequireOnlyProperties(edgeMeta, "weight", "layerRelation", "sameFeatureSlice", "cyclic", "typeOnly", "evidence", "evidenceTruncated");
            var weight = RequirePositiveInt(edgeMeta, "weight");
            var typeOnly = edgeMeta.TryGetProperty("typeOnly", out var typeOnlyEl) && typeOnlyEl.ValueKind == JsonValueKind.True;
            var evidenceList = new List<ArchEdgeEvidence>();
            if (edgeMeta.TryGetProperty("evidence", out var evidenceArr))
            {
                if (evidenceArr.ValueKind != JsonValueKind.Array)
                {
                    throw new JsonException("Edge evidence must be an array.");
                }

                foreach (var evObj in evidenceArr.EnumerateArray())
                {
                    if (evObj.ValueKind != JsonValueKind.Object)
                    {
                        throw new JsonException("Evidence item must be an object.");
                    }

                    RequireOnlyProperties(evObj, "relativePath", "line", "sourceSymbol", "targetSymbol", "roles", "relationship");
                    int? line = null;
                    if (evObj.TryGetProperty("line", out var lineEl) && lineEl.ValueKind != JsonValueKind.Null)
                    {
                        if (lineEl.ValueKind != JsonValueKind.Number || !lineEl.TryGetInt32(out var parsedLine) || parsedLine < 1)
                        {
                            throw new JsonException("Evidence line must be an integer >= 1 or null.");
                        }

                        line = parsedLine;
                    }

                    var roles = ReadOptionalStringArray(evObj, "roles");
                    var relationship = OptionalString(evObj, "relationship") ?? "reference";
                    evidenceList.Add(new ArchEdgeEvidence(
                        RequireString(evObj, "relativePath"),
                        line,
                        OptionalString(evObj, "sourceSymbol"),
                        OptionalString(evObj, "targetSymbol"),
                        roles,
                        relationship));
                }
            }

            rawEdges.Add(new RawArchEdge(source, target, relation, weight, typeOnly, evidenceList));
        }

        return ArchGraphBuilder.Build(id, label, repo, commit, analyzer, granularity, layers, rawNodes, rawEdges, warnings);
    }

    private static void WriteArchGraph(Utf8JsonWriter writer, ArchGraphDocument graph)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("directed", true);
        writer.WriteStartArray("edges");
        foreach (var edge in graph.Edges)
        {
            writer.WriteStartObject();
            writer.WriteBoolean("directed", true);
            writer.WriteString("id", edge.Id);
            writer.WriteStartObject("metadata");
            writer.WriteBoolean("cyclic", edge.Cyclic);
            writer.WriteStartArray("evidence");
            foreach (var ev in edge.Evidence)
            {
                writer.WriteStartObject();
                if (ev.Line is { } line)
                {
                    writer.WriteNumber("line", line);
                }

                writer.WriteString("relationship", ev.Relationship);
                writer.WriteString("relativePath", ev.RelativePath);
                if (ev.Roles.Count > 0)
                {
                    writer.WriteStartArray("roles");
                    foreach (var role in ev.Roles)
                    {
                        writer.WriteStringValue(role);
                    }

                    writer.WriteEndArray();
                }

                if (ev.SourceSymbol is not null)
                {
                    writer.WriteString("sourceSymbol", ev.SourceSymbol);
                }

                if (ev.TargetSymbol is not null)
                {
                    writer.WriteString("targetSymbol", ev.TargetSymbol);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteBoolean("evidenceTruncated", edge.EvidenceTruncated);
            writer.WriteString("layerRelation", edge.LayerRelation);
            writer.WriteBoolean("sameFeatureSlice", edge.SameFeatureSlice);
            writer.WriteBoolean("typeOnly", edge.TypeOnly);
            writer.WriteNumber("weight", edge.Weight);
            writer.WriteEndObject();
            writer.WriteString("relation", edge.Relation);
            writer.WriteString("source", edge.Source);
            writer.WriteString("target", edge.Target);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteString("id", graph.Id);
        writer.WriteString("label", graph.Label);
        writer.WriteStartObject("metadata");
        writer.WriteStartObject("analyzer");
        writer.WriteString("id", graph.Analyzer.Id);
        writer.WriteString("language", graph.Analyzer.Language);
        writer.WriteString("resolution", graph.Analyzer.Resolution);
        writer.WriteString("tool", graph.Analyzer.Tool);
        if (graph.Analyzer.ToolVersion is not null)
        {
            writer.WriteString("toolVersion", graph.Analyzer.ToolVersion);
        }

        writer.WriteEndObject();
        if (graph.Commit is not null)
        {
            writer.WriteString("commit", graph.Commit);
        }

        writer.WriteStartObject("cycles");
        foreach (var (cycleId, cycle) in graph.Cycles)
        {
            writer.WriteStartObject(cycleId);
            writer.WriteStartArray("edgeIds");
            foreach (var edgeId in cycle.EdgeIds)
            {
                writer.WriteStringValue(edgeId);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("members");
            foreach (var member in cycle.Members)
            {
                writer.WriteStringValue(member);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteString("granularity", graph.Granularity);
        if (graph.Layers.Count > 0)
        {
            writer.WriteStartArray("layers");
            foreach (var layer in graph.Layers)
            {
                writer.WriteStringValue(layer);
            }

            writer.WriteEndArray();
        }

        if (graph.Repo is not null)
        {
            writer.WriteString("repo", graph.Repo);
        }

        writer.WriteStartObject("summary");
        writer.WriteNumber("cycleCount", graph.Summary.CycleCount);
        writer.WriteNumber("edgeCount", graph.Summary.EdgeCount);
        writer.WriteNumber("featureCrossEdgeCount", graph.Summary.FeatureCrossEdgeCount);
        writer.WriteNumber("layerViolationCount", graph.Summary.LayerViolationCount);
        writer.WriteNumber("nodeCount", graph.Summary.NodeCount);
        writer.WriteNumber("unassignedNodeCount", graph.Summary.UnassignedNodeCount);
        writer.WriteEndObject();
        if (graph.Warnings.Count > 0)
        {
            writer.WriteStartArray("warnings");
            foreach (var warning in graph.Warnings)
            {
                writer.WriteStringValue(warning);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();

        writer.WriteStartObject("nodes");
        foreach (var (nodeId, node) in graph.Nodes)
        {
            writer.WriteStartObject(nodeId);
            writer.WriteString("label", node.Label);
            writer.WriteStartObject("metadata");
            writer.WriteNumber("fanIn", node.FanIn);
            writer.WriteNumber("fanOut", node.FanOut);
            writer.WriteNumber("fileCount", node.FileCount);
            writer.WriteString("instability", node.Instability);
            writer.WriteString("kind", node.Kind);
            writer.WriteString("language", node.Language);
            if (node.Layer is not null)
            {
                writer.WriteString("layer", node.Layer);
            }

            if (node.Path is not null)
            {
                writer.WriteString("path", node.Path);
            }

            if (node.Project is not null)
            {
                writer.WriteString("project", node.Project);
            }

            if (node.Slice is not null)
            {
                writer.WriteString("slice", node.Slice);
            }

            writer.WriteNumber("symbolCount", node.SymbolCount);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteString("type", ArchGraphBuilder.DatasetType);
        writer.WriteEndObject();
    }

    private static int EdgeRetentionPriority(ArchEdge edge)
    {
        if (edge.Cyclic)
        {
            return 4;
        }

        if (edge.LayerRelation == "up")
        {
            return 3;
        }

        if (!edge.SameFeatureSlice && edge.LayerRelation == "same")
        {
            return 2;
        }

        return 1;
    }

    private static void RequireOnlyProperties(JsonElement element, params string[] allowed)
    {
        var names = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var prop in element.EnumerateObject())
        {
            if (!names.Contains(prop.Name))
            {
                throw new JsonException($"Unknown arch-graph property '{prop.Name}'.");
            }
        }
    }

    private static JsonElement RequireObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"Required object '{name}' is missing or invalid.");
        }

        return value;
    }

    private static JsonElement RequireArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"Required array '{name}' is missing or invalid.");
        }

        return value;
    }

    private static string RequireString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new JsonException($"Required string '{name}' is missing or invalid.");
        }

        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Property '{name}' must be a string.");
        }

        return value.GetString();
    }

    private static int OptionalNonNegativeInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 0)
        {
            throw new JsonException($"Property '{name}' must be a non-negative integer.");
        }

        return number;
    }

    private static int RequirePositiveInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 1)
        {
            throw new JsonException($"Property '{name}' must be an integer >= 1.");
        }

        return number;
    }

    private static List<string> ReadOptionalStringArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"Property '{name}' must be an array of strings.");
        }

        var list = new List<string>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"Property '{name}' entries must be strings.");
            }

            list.Add(item.GetString()!);
        }

        return list;
    }
}
