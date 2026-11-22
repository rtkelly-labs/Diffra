using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Diffra.Protocol;

/// <summary>One cycle that grew or shrank between baseline and candidate.</summary>
public sealed record ArchCycleResize(
    string BaselineCycleId,
    string CandidateCycleId,
    IReadOnlyList<string> BaselineMembers,
    IReadOnlyList<string> CandidateMembers,
    IReadOnlyList<string> AddedMembers,
    IReadOnlyList<string> RemovedMembers);

/// <summary>One edge change between baseline and candidate, classified by structural impact.</summary>
public sealed record ArchEdgeDiff(
    string EdgeId,
    string Change, // added | removed | modified
    string Classification, // cyclic | upward-layer | feature-cross | normal
    ArchEdge? Before,
    ArchEdge? After);

/// <summary>One node change between baseline and candidate.</summary>
public sealed record ArchNodeDiff(
    string NodeId,
    string Change, // added | removed | moved | modified
    ArchNode? Before,
    ArchNode? After);

/// <summary>Scalar count delta for one headline architecture metric.</summary>
public sealed record ArchMetricDelta(string Name, int Before, int After, int Absolute);

/// <summary>Complete, policy-free diff between two architecture graphs, retaining the candidate graph for system-state rendering.</summary>
public sealed record ArchGraphDiff(
    string Status, // unchanged | changed
    ArchGraphDocument Baseline,
    ArchGraphDocument Candidate,
    IReadOnlyList<ArchCycle> CyclesAdded,
    IReadOnlyList<ArchCycle> CyclesRemoved,
    IReadOnlyList<ArchCycleResize> CyclesResized,
    IReadOnlyList<ArchEdgeDiff> EdgeDiffs,
    IReadOnlyList<ArchNodeDiff> NodeDiffs,
    IReadOnlyList<ArchMetricDelta> SummaryDeltas);

/// <summary>Compares two <see cref="ArchGraphDocument"/> snapshots into a factual <see cref="ArchGraphDiff"/> or Delta v1 document.</summary>
public static class ArchGraphComparator
{
    public const string ComparatorId = "diffra.arch-graph";
    public const string ComparatorVersion = "1.0.0";
    public const string EmptyConfigDigest = "sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a";

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Computes the structural diff between <paramref name="baseline"/> and <paramref name="candidate"/>.</summary>
    public static ArchGraphDiff Compare(ArchGraphDocument baseline, ArchGraphDocument candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        // 1. Cycles: match identical IDs first, then pair overlapping removed/added cycles as resizes.
        var removedCycles = baseline.Cycles.Values
            .Where(c => !candidate.Cycles.ContainsKey(c.Id))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
        var addedCycles = candidate.Cycles.Values
            .Where(c => !baseline.Cycles.ContainsKey(c.Id))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

        var cyclesResized = new List<ArchCycleResize>();
        var pairedRemoved = new HashSet<string>(StringComparer.Ordinal);
        var pairedAdded = new HashSet<string>(StringComparer.Ordinal);

        foreach (var oldCycle in removedCycles)
        {
            var oldSet = new HashSet<string>(oldCycle.Members, StringComparer.Ordinal);
            ArchCycle? bestMatch = null;
            var bestOverlap = 0;
            foreach (var newCycle in addedCycles)
            {
                if (pairedAdded.Contains(newCycle.Id))
                {
                    continue;
                }

                var overlap = newCycle.Members.Count(m => oldSet.Contains(m));
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    bestMatch = newCycle;
                }
            }

            if (bestMatch is not null && bestOverlap > 0)
            {
                pairedRemoved.Add(oldCycle.Id);
                pairedAdded.Add(bestMatch.Id);
                var newSet = new HashSet<string>(bestMatch.Members, StringComparer.Ordinal);
                cyclesResized.Add(new ArchCycleResize(
                    oldCycle.Id,
                    bestMatch.Id,
                    oldCycle.Members,
                    bestMatch.Members,
                    bestMatch.Members.Where(m => !oldSet.Contains(m)).Order(StringComparer.Ordinal).ToArray(),
                    oldCycle.Members.Where(m => !newSet.Contains(m)).Order(StringComparer.Ordinal).ToArray()));
            }
        }

        var pureAddedCycles = addedCycles.Where(c => !pairedAdded.Contains(c.Id)).ToArray();
        var pureRemovedCycles = removedCycles.Where(c => !pairedRemoved.Contains(c.Id)).ToArray();

        // 2. Edges.
        var baseEdges = baseline.Edges.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var candEdges = candidate.Edges.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var edgeDiffs = new List<ArchEdgeDiff>();
        foreach (var edgeId in baseEdges.Keys.Union(candEdges.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var inBase = baseEdges.TryGetValue(edgeId, out var before);
            var inCand = candEdges.TryGetValue(edgeId, out var after);
            if (!inBase)
            {
                edgeDiffs.Add(new ArchEdgeDiff(edgeId, "added", ClassifyEdge(after!, candidate), null, after));
            }
            else if (!inCand)
            {
                edgeDiffs.Add(new ArchEdgeDiff(edgeId, "removed", ClassifyEdge(before!, baseline), before, null));
            }
            else if (before!.Weight != after!.Weight
                || !string.Equals(before.Relation, after.Relation, StringComparison.Ordinal)
                || !string.Equals(before.LayerRelation, after.LayerRelation, StringComparison.Ordinal)
                || before.Cyclic != after.Cyclic
                || before.TypeOnly != after.TypeOnly)
            {
                edgeDiffs.Add(new ArchEdgeDiff(edgeId, "modified", ClassifyEdge(after, candidate), before, after));
            }
        }

        // Order edge diffs by structural severity first, then change kind, then ordinal ID.
        edgeDiffs = edgeDiffs
            .OrderByDescending(e => ClassificationRank(e.Classification))
            .ThenBy(e => ChangeRank(e.Change))
            .ThenBy(e => e.EdgeId, StringComparer.Ordinal)
            .ToList();

        // 3. Nodes.
        var nodeDiffs = new List<ArchNodeDiff>();
        foreach (var nodeId in baseline.Nodes.Keys.Union(candidate.Nodes.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var inBase = baseline.Nodes.TryGetValue(nodeId, out var before);
            var inCand = candidate.Nodes.TryGetValue(nodeId, out var after);
            if (!inBase)
            {
                nodeDiffs.Add(new ArchNodeDiff(nodeId, "added", null, after));
            }
            else if (!inCand)
            {
                nodeDiffs.Add(new ArchNodeDiff(nodeId, "removed", before, null));
            }
            else if (!string.Equals(before!.Layer, after!.Layer, StringComparison.Ordinal))
            {
                nodeDiffs.Add(new ArchNodeDiff(nodeId, "moved", before, after));
            }
            else if (before.FanIn != after.FanIn
                || before.FanOut != after.FanOut
                || !string.Equals(before.Instability, after.Instability, StringComparison.Ordinal)
                || !string.Equals(before.Slice, after.Slice, StringComparison.Ordinal))
            {
                nodeDiffs.Add(new ArchNodeDiff(nodeId, "modified", before, after));
            }
        }

        var summaryDeltas = new ArchMetricDelta[]
        {
            new("cycleCount", baseline.Summary.CycleCount, candidate.Summary.CycleCount, candidate.Summary.CycleCount - baseline.Summary.CycleCount),
            new("layerViolationCount", baseline.Summary.LayerViolationCount, candidate.Summary.LayerViolationCount, candidate.Summary.LayerViolationCount - baseline.Summary.LayerViolationCount),
            new("featureCrossEdgeCount", baseline.Summary.FeatureCrossEdgeCount, candidate.Summary.FeatureCrossEdgeCount, candidate.Summary.FeatureCrossEdgeCount - baseline.Summary.FeatureCrossEdgeCount),
            new("edgeCount", baseline.Summary.EdgeCount, candidate.Summary.EdgeCount, candidate.Summary.EdgeCount - baseline.Summary.EdgeCount),
            new("nodeCount", baseline.Summary.NodeCount, candidate.Summary.NodeCount, candidate.Summary.NodeCount - baseline.Summary.NodeCount),
            new("unassignedNodeCount", baseline.Summary.UnassignedNodeCount, candidate.Summary.UnassignedNodeCount, candidate.Summary.UnassignedNodeCount - baseline.Summary.UnassignedNodeCount),
        };

        var hasAnyChange = pureAddedCycles.Length > 0
            || pureRemovedCycles.Length > 0
            || cyclesResized.Count > 0
            || edgeDiffs.Count > 0
            || nodeDiffs.Count > 0;

        return new ArchGraphDiff(
            Status: hasAnyChange ? "changed" : "unchanged",
            Baseline: baseline,
            Candidate: candidate,
            CyclesAdded: pureAddedCycles,
            CyclesRemoved: pureRemovedCycles,
            CyclesResized: cyclesResized,
            EdgeDiffs: edgeDiffs,
            NodeDiffs: nodeDiffs,
            SummaryDeltas: summaryDeltas);
    }

    /// <summary>Serializes an <see cref="ArchGraphDiff"/> into a verified <c>delta-v1.schema.json</c> UTF-8 document.</summary>
    public static byte[] ToDeltaV1Bytes(
        ArchGraphDiff diff,
        string baselineReferenceId,
        string baselineEvidenceId,
        string candidateEvidenceId,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var env = environment ?? new Dictionary<string, string>(StringComparer.Ordinal) { ["runtime"] = "dotnet" };

        var unsigned = WriteDeltaJson(diff, baselineReferenceId, baselineEvidenceId, candidateEvidenceId, env, id: null);
        var id = DocumentIdentity.ComputeId("delta", unsigned);
        var signed = WriteDeltaJson(diff, baselineReferenceId, baselineEvidenceId, candidateEvidenceId, env, id);
        DocumentIdentity.Verify("delta", signed);
        return signed;
    }

    private static byte[] WriteDeltaJson(
        ArchGraphDiff diff,
        string baselineReferenceId,
        string baselineEvidenceId,
        string candidateEvidenceId,
        IReadOnlyDictionary<string, string> environment,
        string? id)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("baselineId", baselineReferenceId);
            writer.WriteString("candidateEvidenceId", candidateEvidenceId);
            writer.WriteStartArray("changes");

            // Emit deterministic JSON-Pointer changes ordered by path: /cycles/..., /edges/..., /nodes/..., /summary/...
            foreach (var cycle in diff.CyclesAdded)
            {
                WriteChange(writer, $"/cycles/{EscapePointer(cycle.Id)}", "added", w =>
                {
                    w.WriteStartObject();
                    w.WriteString("before", (string?)null);
                    w.WriteString("after", string.Join(" -> ", cycle.Members));
                    w.WriteEndObject();
                }, "cycle", m =>
                {
                    m.WriteString("classification", "cyclic");
                    m.WriteNumber("memberCount", cycle.Members.Count);
                });
            }

            foreach (var cycle in diff.CyclesRemoved)
            {
                WriteChange(writer, $"/cycles/{EscapePointer(cycle.Id)}", "removed", w =>
                {
                    w.WriteStartObject();
                    w.WriteString("before", string.Join(" -> ", cycle.Members));
                    w.WriteString("after", (string?)null);
                    w.WriteEndObject();
                }, "cycle", m =>
                {
                    m.WriteString("classification", "cyclic");
                    m.WriteNumber("memberCount", cycle.Members.Count);
                });
            }

            foreach (var resize in diff.CyclesResized)
            {
                WriteChange(writer, $"/cycles/{EscapePointer(resize.CandidateCycleId)}", "modified", w =>
                {
                    w.WriteStartObject();
                    w.WriteString("before", string.Join(" -> ", resize.BaselineMembers));
                    w.WriteString("after", string.Join(" -> ", resize.CandidateMembers));
                    w.WriteEndObject();
                }, "cycle", m =>
                {
                    m.WriteString("baselineCycleId", resize.BaselineCycleId);
                    m.WriteString("classification", "cyclic");
                });
            }

            foreach (var edge in diff.EdgeDiffs)
            {
                var beforeWeight = edge.Before?.Weight.ToString(CultureInfo.InvariantCulture);
                var afterWeight = edge.After?.Weight.ToString(CultureInfo.InvariantCulture);
                var absolute = edge.Before is not null && edge.After is not null
                    ? (edge.After.Weight - edge.Before.Weight).ToString(CultureInfo.InvariantCulture)
                    : null;
                var sampleEdge = edge.After ?? edge.Before;
                var sample = sampleEdge is { Evidence.Count: > 0 } ? sampleEdge.Evidence[0].FormatCompact() : null;
                WriteChange(writer, $"/edges/{EscapePointer(edge.EdgeId)}", edge.Change, w =>
                {
                    w.WriteStartObject();
                    if (beforeWeight is not null)
                    {
                        w.WriteString("before", beforeWeight);
                    }

                    if (afterWeight is not null)
                    {
                        w.WriteString("after", afterWeight);
                    }

                    if (absolute is not null)
                    {
                        w.WriteString("absolute", absolute);
                    }

                    w.WriteEndObject();
                }, "references", m =>
                {
                    m.WriteString("classification", edge.Classification);
                    if (sample is not null)
                    {
                        m.WriteString("evidence", sample);
                    }
                });
            }

            foreach (var node in diff.NodeDiffs)
            {
                var deltaKind = node.Change == "moved" ? "modified" : node.Change;
                WriteChange(writer, $"/nodes/{EscapePointer(node.NodeId)}", deltaKind, w =>
                {
                    w.WriteStartObject();
                    if (node.Before is not null)
                    {
                        w.WriteString("before", node.Before.Layer ?? "unassigned");
                    }

                    if (node.After is not null)
                    {
                        w.WriteString("after", node.After.Layer ?? "unassigned");
                    }

                    w.WriteEndObject();
                }, "layer", m =>
                {
                    m.WriteString("nodeChange", node.Change);
                    if (node.After is not null)
                    {
                        m.WriteString("instability", node.After.Instability);
                    }
                });
            }

            foreach (var metric in diff.SummaryDeltas.Where(d => d.Absolute != 0))
            {
                WriteChange(writer, $"/summary/{metric.Name}", "modified", w =>
                {
                    w.WriteStartObject();
                    w.WriteString("before", metric.Before.ToString(CultureInfo.InvariantCulture));
                    w.WriteString("after", metric.After.ToString(CultureInfo.InvariantCulture));
                    w.WriteString("absolute", metric.Absolute.ToString(CultureInfo.InvariantCulture));
                    w.WriteEndObject();
                }, "count", null);
            }

            writer.WriteEndArray();
            writer.WriteStartObject("comparator");
            writer.WriteString("configDigest", EmptyConfigDigest);
            writer.WriteString("id", ComparatorId);
            writer.WriteString("inputTypeId", "diffra.arch-graph");
            writer.WriteString("inputTypeVersion", "1.0.0");
            writer.WriteString("version", ComparatorVersion);
            writer.WriteEndObject();
            if (id is not null)
            {
                writer.WriteString("id", id);
            }

            writer.WriteStartObject("provenance");
            writer.WriteString("baselineEvidenceId", baselineEvidenceId);
            writer.WriteString("candidateEvidenceId", candidateEvidenceId);
            writer.WriteStartObject("environment");
            foreach (var (k, v) in environment.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WriteString(k, v);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteString("schema", StructuredNumericComparator.DeltaSchema);
            writer.WriteString("status", diff.Status);
            writer.WriteString("typeResolution", "recognized");
            writer.WriteEndObject();
        }

        return CanonicalJson.Canonicalize(stream.ToArray());
    }

    private static void WriteChange(
        Utf8JsonWriter writer,
        string path,
        string kind,
        Action<Utf8JsonWriter> writeValue,
        string? unit,
        Action<Utf8JsonWriter>? writeMetadata)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", kind);
        if (writeMetadata is not null)
        {
            writer.WriteStartObject("metadata");
            writeMetadata(writer);
            writer.WriteEndObject();
        }

        writer.WriteString("path", path);
        if (unit is not null)
        {
            writer.WriteString("unit", unit);
        }

        writer.WritePropertyName("value");
        writeValue(writer);
        writer.WriteEndObject();
    }

    private static string ClassifyEdge(ArchEdge edge, ArchGraphDocument graph)
    {
        if (edge.Cyclic)
        {
            return "cyclic";
        }

        if (edge.LayerRelation == "up")
        {
            return "upward-layer";
        }

        if (graph.Nodes.TryGetValue(edge.Source, out var src)
            && graph.Nodes.TryGetValue(edge.Target, out var dst)
            && string.Equals(src.Layer, "features", StringComparison.Ordinal)
            && string.Equals(dst.Layer, "features", StringComparison.Ordinal)
            && !edge.SameFeatureSlice)
        {
            return "feature-cross";
        }

        return "normal";
    }

    private static int ClassificationRank(string classification) => classification switch
    {
        "cyclic" => 4,
        "upward-layer" => 3,
        "feature-cross" => 2,
        _ => 1,
    };

    private static int ChangeRank(string change) => change switch
    {
        "added" => 0,
        "modified" => 1,
        "removed" => 2,
        _ => 3,
    };

    private static string EscapePointer(string token) =>
        token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
