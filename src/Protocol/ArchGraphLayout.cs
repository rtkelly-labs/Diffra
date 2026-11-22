using System.Globalization;

namespace Diffra.Protocol;

/// <summary>One architectural layer band in the laid-out DAG.</summary>
public sealed record ArchLayoutBand(
    int Index,
    string Id,
    string Label,
    double X,
    double Y,
    double Width,
    double Height);

/// <summary>One positioned node in the layered architecture DAG.</summary>
public sealed record ArchLayoutNode(
    string Id,
    string Label,
    string LayerId,
    int BandIndex,
    int SlotIndex,
    double X,
    double Y,
    double Width,
    double Height);

/// <summary>One routed edge in the layered architecture DAG.</summary>
public sealed record ArchLayoutEdge(
    string EdgeId,
    string Source,
    string Target,
    double SourceX,
    double SourceY,
    double TargetX,
    double TargetY,
    bool BackEdge,
    double DipX,
    string SvgPath);

/// <summary>Deterministic top-to-bottom layered DAG layout mirroring <c>@rtkelly-labs/design-system</c> geometry.</summary>
public sealed record ArchGraphLayoutResult(
    IReadOnlyList<ArchLayoutBand> Bands,
    IReadOnlyList<ArchLayoutNode> Nodes,
    IReadOnlyList<ArchLayoutEdge> Edges,
    double Width,
    double Height);

/// <summary>
/// Deterministic top-to-bottom Sugiyama-style layered layout with pinned architectural layer bands:
/// nodes stay inside their declared layer band (or longest-path topological rank when unlayered),
/// reordered within each band by 2-pass barycentre sweeps with ordinal tie-breaking.
/// </summary>
public static class ArchGraphLayout
{
    public const double NodeWidth = 176.0;
    public const double NodeHeight = 54.0;
    public const double ColumnGap = 24.0;
    public const double BandGap = 64.0;
    public const double BandPaddingX = 24.0;
    public const double BandHeaderHeight = 26.0;
    public const double BandPaddingBottom = 16.0;
    public const double CanvasMargin = 20.0;

    /// <summary>Computes a deterministic layout for <paramref name="nodeIds"/>, their optional layers, and directed edges.</summary>
    public static ArchGraphLayoutResult Compute(
        IReadOnlyList<string> declaredLayers,
        IReadOnlyDictionary<string, (string Label, string? Layer)> nodes,
        IReadOnlyList<(string EdgeId, string Source, string Target)> edges)
    {
        ArgumentNullException.ThrowIfNull(declaredLayers);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        var ids = nodes.Keys.Order(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return new ArchGraphLayoutResult([], [], [], 320, 120);
        }

        var known = new HashSet<string>(ids, StringComparer.Ordinal);
        var validEdges = edges
            .Where(e => !string.Equals(e.Source, e.Target, StringComparison.Ordinal) && known.Contains(e.Source) && known.Contains(e.Target))
            .OrderBy(e => e.EdgeId, StringComparer.Ordinal)
            .ToList();

        var (bandIds, bandMap) = AssignBands(declaredLayers, nodes, ids, validEdges);
        var bands = Enumerable.Range(0, bandIds.Count).Select(_ => new List<string>()).ToList();
        foreach (var id in ids)
        {
            bands[bandMap[id]].Add(id);
        }

        // Drop empty bands while preserving declared order.
        var nonEmptyBandIds = new List<string>();
        var nonEmptyBands = new List<List<string>>();
        for (var i = 0; i < bands.Count; i++)
        {
            if (bands[i].Count > 0)
            {
                nonEmptyBandIds.Add(bandIds[i]);
                nonEmptyBands.Add(bands[i]);
            }
        }

        // Re-index bandMap to compacted bands.
        var compactBandOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var b = 0; b < nonEmptyBands.Count; b++)
        {
            foreach (var id in nonEmptyBands[b])
            {
                compactBandOf[id] = b;
            }
        }

        // Build forward-only neighbour lists for barycentre sweeps.
        var preds = ids.ToDictionary(k => k, _ => new List<string>(), StringComparer.Ordinal);
        var succs = ids.ToDictionary(k => k, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (_, source, target) in validEdges)
        {
            if (compactBandOf[target] > compactBandOf[source])
            {
                succs[source].Add(target);
                preds[target].Add(source);
            }
        }

        var slot = new Dictionary<string, int>(StringComparer.Ordinal);
        void Reindex()
        {
            for (var b = 0; b < nonEmptyBands.Count; b++)
            {
                for (var s = 0; s < nonEmptyBands[b].Count; s++)
                {
                    slot[nonEmptyBands[b][s]] = s;
                }
            }
        }

        Reindex();
        void Reorder(List<string> layer, Dictionary<string, List<string>> neighbours)
        {
            var keyed = layer.Select(id =>
            {
                var list = neighbours[id];
                var mean = list.Count > 0 ? list.Average(other => (double)slot[other]) : slot[id];
                return (Id: id, Key: mean, Current: slot[id]);
            })
            .OrderBy(item => item.Key)
            .ThenBy(item => item.Current)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToList();

            layer.Clear();
            for (var i = 0; i < keyed.Count; i++)
            {
                layer.Add(keyed[i].Id);
                slot[keyed[i].Id] = i;
            }
        }

        for (var sweep = 0; sweep < 2; sweep++)
        {
            for (var b = 1; b < nonEmptyBands.Count; b++)
            {
                Reorder(nonEmptyBands[b], preds);
            }

            for (var b = nonEmptyBands.Count - 2; b >= 0; b--)
            {
                Reorder(nonEmptyBands[b], succs);
            }
        }

        // Compute coordinates: top-to-bottom layer bands, horizontally centered on the widest band.
        var widestCount = nonEmptyBands.Max(b => b.Count);
        var innerWidth = widestCount * NodeWidth + Math.Max(0, widestCount - 1) * ColumnGap;
        var bandWidth = innerWidth + 2 * BandPaddingX;
        var backGutter = 56.0;
        var bandHeight = BandHeaderHeight + NodeHeight + BandPaddingBottom;
        var canvasWidth = CanvasMargin * 2 + bandWidth + backGutter;

        var layoutBands = new List<ArchLayoutBand>(nonEmptyBands.Count);
        var layoutNodes = new List<ArchLayoutNode>(ids.Count);
        var placed = new Dictionary<string, ArchLayoutNode>(StringComparer.Ordinal);

        for (var b = 0; b < nonEmptyBands.Count; b++)
        {
            var bandY = CanvasMargin + b * (bandHeight + BandGap);
            var bandId = nonEmptyBandIds[b];
            layoutBands.Add(new ArchLayoutBand(b, bandId, bandId, CanvasMargin, bandY, bandWidth, bandHeight));

            var row = nonEmptyBands[b];
            var rowWidth = row.Count * NodeWidth + Math.Max(0, row.Count - 1) * ColumnGap;
            var startX = CanvasMargin + BandPaddingX + (innerWidth - rowWidth) / 2.0;
            var nodeY = bandY + BandHeaderHeight;

            for (var s = 0; s < row.Count; s++)
            {
                var nodeId = row[s];
                var nodeX = startX + s * (NodeWidth + ColumnGap);
                var placedNode = new ArchLayoutNode(nodeId, nodes[nodeId].Label, bandId, b, s, nodeX, nodeY, NodeWidth, NodeHeight);
                layoutNodes.Add(placedNode);
                placed[nodeId] = placedNode;
            }
        }

        var canvasHeight = CanvasMargin * 2 + nonEmptyBands.Count * bandHeight + Math.Max(0, nonEmptyBands.Count - 1) * BandGap;

        var layoutEdges = new List<ArchLayoutEdge>(validEdges.Count);
        var backIndex = 0;
        foreach (var (edgeId, source, target) in validEdges)
        {
            var a = placed[source];
            var b = placed[target];
            if (b.BandIndex > a.BandIndex)
            {
                var sx = a.X + a.Width / 2.0;
                var sy = a.Y + a.Height;
                var tx = b.X + b.Width / 2.0;
                var ty = b.Y;
                var dy = (ty - sy) / 2.0;
                var path = string.Create(CultureInfo.InvariantCulture,
                    $"M {sx:0.#} {sy:0.#} C {sx:0.#} {sy + dy:0.#}, {tx:0.#} {ty - dy:0.#}, {tx:0.#} {ty:0.#}");
                layoutEdges.Add(new ArchLayoutEdge(edgeId, source, target, sx, sy, tx, ty, false, 0, path));
            }
            else if (b.BandIndex == a.BandIndex)
            {
                // Same-layer edge (e.g., feature-to-feature): arc slightly above the node bottoms.
                var leftToRight = b.X >= a.X;
                var sx = leftToRight ? a.X + a.Width : a.X;
                var tx = leftToRight ? b.X : b.X + b.Width;
                var sy = a.Y + a.Height / 2.0;
                var ty = b.Y + b.Height / 2.0;
                var lift = Math.Abs(b.SlotIndex - a.SlotIndex) > 1 ? 26.0 : 0.0;
                var midX = (sx + tx) / 2.0;
                var path = lift > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"M {sx:0.#} {sy:0.#} Q {midX:0.#} {sy - lift:0.#}, {tx:0.#} {ty:0.#}")
                    : string.Create(CultureInfo.InvariantCulture, $"M {sx:0.#} {sy:0.#} L {tx:0.#} {ty:0.#}");
                layoutEdges.Add(new ArchLayoutEdge(edgeId, source, target, sx, sy, tx, ty, true, midX, path));
            }
            else
            {
                // Upward edge (layer violation or cycle back-edge): route cleanly along the right gutter.
                var sx = a.X + a.Width;
                var sy = a.Y + a.Height / 2.0;
                var tx = b.X + b.Width;
                var ty = b.Y + b.Height / 2.0;
                var gutterX = CanvasMargin + bandWidth + 14.0 + (backIndex % 4) * 10.0;
                backIndex++;
                var path = string.Create(CultureInfo.InvariantCulture,
                    $"M {sx:0.#} {sy:0.#} C {gutterX:0.#} {sy:0.#}, {gutterX:0.#} {ty:0.#}, {tx:0.#} {ty:0.#}");
                layoutEdges.Add(new ArchLayoutEdge(edgeId, source, target, sx, sy, tx, ty, true, gutterX, path));
            }
        }

        return new ArchGraphLayoutResult(layoutBands, layoutNodes, layoutEdges, canvasWidth, canvasHeight);
    }

    private static (List<string> BandIds, Dictionary<string, int> BandOf) AssignBands(
        IReadOnlyList<string> declaredLayers,
        IReadOnlyDictionary<string, (string Label, string? Layer)> nodes,
        List<string> ids,
        List<(string EdgeId, string Source, string Target)> edges)
    {
        if (declaredLayers.Count > 0)
        {
            var bandIds = declaredLayers.ToList();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < bandIds.Count; i++)
            {
                index[bandIds[i]] = i;
            }

            var unassignedIndex = bandIds.Count;
            var hasUnassigned = false;
            var bandOf = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var layer = nodes[id].Layer;
                if (layer is not null && index.TryGetValue(layer, out var band))
                {
                    bandOf[id] = band;
                }
                else
                {
                    bandOf[id] = unassignedIndex;
                    hasUnassigned = true;
                }
            }

            if (hasUnassigned)
            {
                bandIds.Add("unassigned");
            }

            return (bandIds, bandOf);
        }

        // Unlayered fallback: break cycles by DFS (ordinal order), then Kahn longest-path topological rank.
        var outMap = ids.ToDictionary(k => k, _ => new List<string>(), StringComparer.Ordinal);
        var indegree = ids.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);
        foreach (var (_, source, target) in edges)
        {
            outMap[source].Add(target);
            indegree[target]++;
        }

        foreach (var list in outMap.Values)
        {
            list.Sort(StringComparer.Ordinal);
        }

        var reversed = new HashSet<(string, string)>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = open, 2 = done
        void Visit(string node)
        {
            state[node] = 1;
            foreach (var next in outMap[node])
            {
                if (state.TryGetValue(next, out var s) && s == 1)
                {
                    reversed.Add((node, next));
                }
                else if (!state.ContainsKey(next))
                {
                    Visit(next);
                }
            }

            state[node] = 2;
        }

        foreach (var id in ids.Where(id => indegree[id] == 0))
        {
            if (!state.ContainsKey(id))
            {
                Visit(id);
            }
        }

        foreach (var id in ids)
        {
            if (!state.ContainsKey(id))
            {
                Visit(id);
            }
        }

        var forwardSucc = ids.ToDictionary(k => k, _ => new List<string>(), StringComparer.Ordinal);
        var remaining = ids.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);
        foreach (var (_, source, target) in edges)
        {
            if (!reversed.Contains((source, target)))
            {
                forwardSucc[source].Add(target);
                remaining[target]++;
            }
        }

        var rank = ids.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);
        var queue = new Queue<string>(ids.Where(id => remaining[id] == 0));
        while (queue.TryDequeue(out var current))
        {
            foreach (var next in forwardSucc[current])
            {
                rank[next] = Math.Max(rank[next], rank[current] + 1);
                remaining[next]--;
                if (remaining[next] == 0)
                {
                    queue.Enqueue(next);
                }
            }
        }

        var maxRank = rank.Values.DefaultIfEmpty(0).Max();
        var generatedBands = Enumerable.Range(0, maxRank + 1)
            .Select(r => $"layer-{r.ToString(CultureInfo.InvariantCulture)}")
            .ToList();
        return (generatedBands, rank);
    }
}
