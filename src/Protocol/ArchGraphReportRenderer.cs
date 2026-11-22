using System.Globalization;
using System.Net;
using System.Text;

namespace Diffra.Protocol;

/// <summary>
/// Renders deterministic architecture graph reports for both current system state and commit-to-commit
/// diffs in HTML (zero-JS, <c>@rtkelly-labs/design-system</c> tokens, embedded SVG), standalone SVG,
/// Markdown, PR Comment, and token-budgeted Agent Markdown.
/// </summary>
public static class ArchGraphReportRenderer
{
    private const string HtmlCss = """
        :root,[data-theme="sketch"]{--ds-surface-base:#f7f9fb;--ds-surface-raised:#ffffff;--ds-surface-sunken:#eef3f7;--ds-text-primary:#18212b;--ds-text-secondary:#4b5563;--ds-text-muted:#6b7280;--ds-border-strong:#18212b;--ds-border-default:#d5dde5;--ds-border-subtle:#e5ebf0;--ds-intent-success:#107c41;--ds-intent-warning:#b45309;--ds-intent-danger:#b91c1c;--ds-intent-info:#0369a1;--ds-delta-added:#15803d;--ds-delta-removed:#b91c1c;--ds-delta-moved:#0369a1}
        @media(prefers-color-scheme:dark){:root:not([data-theme="sketch"]){--ds-surface-base:#0f172a;--ds-surface-raised:#1e293b;--ds-surface-sunken:#090d16;--ds-text-primary:#f8fafc;--ds-text-secondary:#cbd5e1;--ds-text-muted:#94a3b8;--ds-border-strong:#f8fafc;--ds-border-default:#334155;--ds-border-subtle:#263244;--ds-intent-success:#22c55e;--ds-intent-warning:#f59e0b;--ds-intent-danger:#ef4444;--ds-intent-info:#38bdf8;--ds-delta-added:#22c55e;--ds-delta-removed:#ef4444;--ds-delta-moved:#38bdf8}}
        [data-theme="midnight"]{--ds-surface-base:#0f172a;--ds-surface-raised:#1e293b;--ds-surface-sunken:#090d16;--ds-text-primary:#f8fafc;--ds-text-secondary:#cbd5e1;--ds-text-muted:#94a3b8;--ds-border-strong:#f8fafc;--ds-border-default:#334155;--ds-border-subtle:#263244;--ds-intent-success:#22c55e;--ds-intent-warning:#f59e0b;--ds-intent-danger:#ef4444;--ds-intent-info:#38bdf8;--ds-delta-added:#22c55e;--ds-delta-removed:#ef4444;--ds-delta-moved:#38bdf8}
        body{font-family:system-ui,-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif;line-height:1.5;margin:0 auto;max-width:78rem;padding:1.5rem;color:var(--ds-text-primary);background:var(--ds-surface-base)}
        main{background:var(--ds-surface-raised);border:1px solid var(--ds-border-default);border-radius:.5rem;padding:clamp(1rem,3vw,2rem)}
        h1,h2,h3{line-height:1.2}h1{margin-top:0;font-size:1.65rem}
        .verdict-band{display:flex;align-items:center;justify-content:space-between;gap:1rem;border-left:6px solid var(--ds-border-strong);background:var(--ds-surface-sunken);padding:.9rem 1.2rem;margin:1rem 0;border-radius:.25rem}
        .verdict-danger{border-color:var(--ds-intent-danger)}.verdict-warn{border-color:var(--ds-intent-warning)}.verdict-neutral{border-color:var(--ds-border-strong)}
        .verdict-title{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;font-size:1.05rem;font-weight:800;letter-spacing:.05em;text-transform:uppercase}
        .provenance{display:flex;flex-wrap:wrap;gap:.5rem 1.25rem;font-family:ui-monospace,SFMono-Regular,Consolas,monospace;font-size:.78rem;color:var(--ds-text-secondary);padding:.6rem 0;border-top:1px solid var(--ds-border-subtle);border-bottom:1px solid var(--ds-border-subtle);margin-bottom:1rem}
        .stat-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(10rem,1fr));gap:.75rem;margin:1rem 0}
        .stat-card{background:var(--ds-surface-sunken);border:1px solid var(--ds-border-default);border-radius:.375rem;padding:.7rem .95rem}
        .stat-label{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;font-size:.72rem;font-weight:700;color:var(--ds-text-muted);letter-spacing:.05em;text-transform:uppercase}
        .stat-value{font-size:1.4rem;font-weight:800;margin-top:.2rem;font-variant-numeric:tabular-nums}
        .stat-delta{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;font-size:.82rem;margin-left:.45rem}
        .delta-added{color:var(--ds-delta-added)}.delta-removed{color:var(--ds-delta-removed)}.delta-moved{color:var(--ds-delta-moved)}.delta-muted{color:var(--ds-text-muted)}
        .intent-danger{color:var(--ds-intent-danger)}.intent-warning{color:var(--ds-intent-warning)}.intent-success{color:var(--ds-intent-success)}
        .view-tabs input[type="radio"]{position:absolute;opacity:0;pointer-events:none}
        .view-tabs .tab-bar{display:flex;gap:.5rem;margin:.75rem 0;border-bottom:2px solid var(--ds-border-default)}
        .view-tabs .tab-bar label{cursor:pointer;padding:.45rem .85rem;font-family:ui-monospace,SFMono-Regular,Consolas,monospace;font-size:.8rem;font-weight:700;text-transform:uppercase;color:var(--ds-text-muted);border-bottom:3px solid transparent;margin-bottom:-2px}
        #tab-diff:checked ~ .tab-bar label[for="tab-diff"],#tab-current:checked ~ .tab-bar label[for="tab-current"]{color:var(--ds-text-primary);border-bottom-color:var(--ds-border-strong)}
        .tab-panel{display:none;overflow-x:auto;border:1px solid var(--ds-border-default);border-radius:.375rem;background:var(--ds-surface-sunken);padding:.5rem}
        #tab-diff:checked ~ .panel-diff,#tab-current:checked ~ .panel-current{display:block}
        .legend{display:flex;flex-wrap:wrap;gap:.5rem 1.1rem;font-family:ui-monospace,SFMono-Regular,Consolas,monospace;font-size:.76rem;color:var(--ds-text-secondary);margin:.5rem 0}
        .swatch{display:inline-block;width:1.1rem;height:.2rem;vertical-align:middle;margin-right:.3rem}
        table{border-collapse:collapse;width:100%;margin:1rem 0;border-top:2px solid var(--ds-border-strong);font-size:.88rem}
        caption{text-align:left;font-family:ui-monospace,SFMono-Regular,Consolas,monospace;font-size:.78rem;font-weight:700;text-transform:uppercase;letter-spacing:.05em;color:var(--ds-text-muted);padding:.5rem .35rem}
        th,td{border-bottom:1px solid var(--ds-border-subtle);padding:.45rem .6rem;text-align:left;vertical-align:top}
        th{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;font-size:.75rem;text-transform:uppercase;color:var(--ds-text-muted);border-bottom:1px solid var(--ds-border-default)}
        .mono{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;overflow-wrap:anywhere}
        .num{text-align:right;font-variant-numeric:tabular-nums;white-space:nowrap}
        .strike{text-decoration:line-through}
        .badge{display:inline-block;padding:.1rem .4rem;border-radius:.2rem;font-family:ui-monospace,SFMono-Regular,Consolas,monospace;font-size:.72rem;font-weight:700;text-transform:uppercase;border:1px solid var(--ds-border-default)}
        .badge-danger{color:var(--ds-intent-danger);border-color:var(--ds-intent-danger)}
        .badge-warn{color:var(--ds-intent-warning);border-color:var(--ds-intent-warning)}
        .badge-added{color:var(--ds-delta-added);border-color:var(--ds-delta-added)}
        .badge-removed{color:var(--ds-delta-removed);border-color:var(--ds-delta-removed)}
        """;

    /// <summary>Renders a single architecture graph (current system state) as a standalone SVG.</summary>
    public static string RenderCurrentSvg(ArchGraphDocument graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var nodeInput = graph.Nodes.ToDictionary(
            kv => kv.Key,
            kv => (kv.Value.Label, kv.Value.Layer),
            StringComparer.Ordinal);
        var edgeInput = graph.Edges.Select(e => (e.Id, e.Source, e.Target)).ToList();
        var layout = ArchGraphLayout.Compute(graph.Layers, nodeInput, edgeInput);
        var cyclicNodes = new HashSet<string>(graph.Cycles.Values.SelectMany(c => c.Members), StringComparer.Ordinal);
        var edgeById = graph.Edges.ToDictionary(e => e.Id, StringComparer.Ordinal);

        var svg = new StringBuilder(4096);
        BeginSvg(svg, layout, $"Architecture map for {graph.Label}");
        RenderBandsSvg(svg, layout);

        svg.Append("<g data-layer=\"edges\">");
        foreach (var le in layout.Edges)
        {
            var edge = edgeById[le.EdgeId];
            var (stroke, marker, dash, width) = edge switch
            {
                { Cyclic: true } => ("var(--ds-intent-danger)", "arrow-danger", "6 3", "2.4"),
                { LayerRelation: "up" } => ("var(--ds-intent-danger)", "arrow-danger", "", "2.2"),
                { SameFeatureSlice: false } when IsFeatureCross(edge, graph) => ("var(--ds-intent-warning)", "arrow-warn", "", "2"),
                { TypeOnly: true } => ("var(--ds-text-muted)", "arrow-muted", "3 3", "1.3"),
                _ => ("var(--ds-text-secondary)", "arrow-muted", "", "1.5"),
            };
            WriteEdgePathSvg(svg, le.SvgPath, stroke, marker, dash, width, "0.85", $"{edge.Source} -> {edge.Target} ({edge.Weight} refs, {edge.LayerRelation})");
        }

        svg.Append("</g>");

        svg.Append("<g data-layer=\"nodes\">");
        foreach (var ln in layout.Nodes)
        {
            var node = graph.Nodes[ln.Id];
            var cyclic = cyclicNodes.Contains(ln.Id);
            var border = cyclic ? "var(--ds-intent-danger)" : "var(--ds-border-strong)";
            var subtitle = $"in {node.FanIn} · out {node.FanOut} · I={node.Instability}";
            WriteNodeBoxSvg(svg, ln, border, "var(--ds-surface-raised)", "1", subtitle, cyclic ? "CYCLE" : null, "var(--ds-intent-danger)");
        }

        svg.Append("</g></svg>");
        return svg.ToString();
    }

    /// <summary>Renders the current system state as a self-contained static HTML page.</summary>
    public static string RenderCurrentHtml(ArchGraphDocument graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var sb = new StringBuilder(8192);
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>")
            .Append(Escape($"Architecture state — {graph.Label}"))
            .Append("</title><style>").Append(HtmlCss).Append("</style></head><body><main>");

        sb.Append("<h1>Architecture state: ").Append(Escape(graph.Label)).Append("</h1>");
        sb.Append("<div class=\"provenance\">");
        sb.Append("<span>target: ").Append(Escape(graph.Id)).Append("</span>");
        if (graph.Repo is not null)
        {
            sb.Append("<span>repo: ").Append(Escape(graph.Repo)).Append("</span>");
        }

        if (graph.Commit is not null)
        {
            sb.Append("<span>commit: ").Append(Escape(ShortCommit(graph.Commit))).Append("</span>");
        }

        sb.Append("<span>analyzer: ").Append(Escape(graph.Analyzer.Tool)).Append(" (").Append(Escape(graph.Analyzer.Resolution)).Append(")</span>");
        sb.Append("<span>language: ").Append(Escape(graph.Analyzer.Language)).Append("</span>");
        sb.Append("</div>");

        sb.Append("<div class=\"stat-grid\">");
        AppendStatCard(sb, "Nodes", graph.Summary.NodeCount, null);
        AppendStatCard(sb, "Edges", graph.Summary.EdgeCount, null);
        AppendStatCard(sb, "Cycles", graph.Summary.CycleCount, null, dangerWhenPositive: true);
        AppendStatCard(sb, "Layer violations", graph.Summary.LayerViolationCount, null, dangerWhenPositive: true);
        AppendStatCard(sb, "Feature cross-edges", graph.Summary.FeatureCrossEdgeCount, null, warnWhenPositive: true);
        sb.Append("</div>");

        sb.Append("<section><h2>Layered dependency graph</h2><div class=\"tab-panel\" style=\"display:block\">")
            .Append(RenderCurrentSvg(graph))
            .Append("</div></section>");

        if (graph.Cycles.Count > 0)
        {
            sb.Append("<section><h2>Cycles</h2><table><caption>Strongly connected components</caption><thead><tr><th>Cycle</th><th>Members</th><th class=\"num\">Edges</th></tr></thead><tbody>");
            foreach (var cycle in graph.Cycles.Values)
            {
                sb.Append("<tr><td class=\"mono intent-danger\">").Append(Escape(cycle.Id)).Append("</td><td class=\"mono\">")
                    .Append(Escape(string.Join(" → ", cycle.Members))).Append("</td><td class=\"num\">")
                    .Append(cycle.EdgeIds.Count.ToString(CultureInfo.InvariantCulture)).Append("</td></tr>");
            }

            sb.Append("</tbody></table></section>");
        }

        sb.Append("<section><h2>Modules</h2><table><caption>All modules in current system</caption><thead><tr><th>Module</th><th>Layer</th><th class=\"num\">Fan-in</th><th class=\"num\">Fan-out</th><th class=\"num\">Instability</th></tr></thead><tbody>");
        foreach (var node in graph.Nodes.Values)
        {
            sb.Append("<tr><td class=\"mono\">").Append(Escape(node.Id)).Append("</td><td class=\"mono\">")
                .Append(Escape(node.Layer ?? "unassigned")).Append("</td><td class=\"num\">")
                .Append(node.FanIn.ToString(CultureInfo.InvariantCulture)).Append("</td><td class=\"num\">")
                .Append(node.FanOut.ToString(CultureInfo.InvariantCulture)).Append("</td><td class=\"num mono\">")
                .Append(Escape(node.Instability)).Append("</td></tr>");
        }

        sb.Append("</tbody></table></section></main></body></html>");
        return sb.ToString();
    }

    /// <summary>Renders the System State + Diff Overlay SVG (current system as low-contrast context, diff overlaid in delta/intent roles).</summary>
    public static string RenderDiffOverlaySvg(ArchGraphDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var unionNodes = new Dictionary<string, (string Label, string? Layer)>(StringComparer.Ordinal);
        foreach (var (id, node) in diff.Candidate.Nodes)
        {
            unionNodes[id] = (node.Label, node.Layer);
        }

        foreach (var (id, node) in diff.Baseline.Nodes)
        {
            unionNodes.TryAdd(id, (node.Label, node.Layer));
        }

        var unionEdges = new SortedDictionary<string, (string EdgeId, string Source, string Target)>(StringComparer.Ordinal);
        foreach (var edge in diff.Candidate.Edges)
        {
            unionEdges[edge.Id] = (edge.Id, edge.Source, edge.Target);
        }

        foreach (var edge in diff.Baseline.Edges)
        {
            unionEdges.TryAdd(edge.Id, (edge.Id, edge.Source, edge.Target));
        }

        var layers = diff.Candidate.Layers.Count > 0 ? diff.Candidate.Layers : diff.Baseline.Layers;
        var layout = ArchGraphLayout.Compute(layers, unionNodes, unionEdges.Values.ToList());

        var edgeDiffMap = diff.EdgeDiffs.ToDictionary(e => e.EdgeId, StringComparer.Ordinal);
        var nodeDiffMap = diff.NodeDiffs.ToDictionary(n => n.NodeId, StringComparer.Ordinal);
        var newCycleNodes = new HashSet<string>(
            diff.CyclesAdded.SelectMany(c => c.Members).Concat(diff.CyclesResized.SelectMany(r => r.AddedMembers)),
            StringComparer.Ordinal);

        var svg = new StringBuilder(4096);
        BeginSvg(svg, layout, $"Architecture diff overlay for {diff.Candidate.Label}");
        RenderBandsSvg(svg, layout);

        // Draw unchanged current-system context edges first at low opacity, then overlay diff edges on top.
        svg.Append("<g data-layer=\"context-edges\">");
        foreach (var le in layout.Edges)
        {
            if (edgeDiffMap.ContainsKey(le.EdgeId))
            {
                continue;
            }

            WriteEdgePathSvg(svg, le.SvgPath, "var(--ds-text-muted)", "arrow-muted", "", "1.2", "0.24", $"{le.Source} -> {le.Target} (unchanged)");
        }

        svg.Append("</g><g data-layer=\"diff-edges\">");
        foreach (var le in layout.Edges)
        {
            if (!edgeDiffMap.TryGetValue(le.EdgeId, out var edgeDiff))
            {
                continue;
            }

            var (stroke, marker, dash, width) = edgeDiff.Change switch
            {
                "added" when edgeDiff.Classification is "cyclic" or "upward-layer" => ("var(--ds-intent-danger)", "arrow-danger", "", "2.8"),
                "added" when edgeDiff.Classification == "feature-cross" => ("var(--ds-intent-warning)", "arrow-warn", "", "2.5"),
                "added" => ("var(--ds-delta-added)", "arrow-added", "", "2.4"),
                "removed" => ("var(--ds-delta-removed)", "arrow-removed", "6 4", "2.2"),
                _ => ("var(--ds-delta-moved)", "arrow-moved", "", "2.0"),
            };

            var title = $"{edgeDiff.Change} {edgeDiff.EdgeId} [{edgeDiff.Classification}]";
            WriteEdgePathSvg(svg, le.SvgPath, stroke, marker, dash, width, "0.95", title);
        }

        svg.Append("</g><g data-layer=\"nodes\">");
        foreach (var ln in layout.Nodes)
        {
            nodeDiffMap.TryGetValue(ln.Id, out var nodeDiff);
            var isTouched = nodeDiff is not null
                || diff.EdgeDiffs.Any(e => (e.After ?? e.Before) is { } edge && (edge.Source == ln.Id || edge.Target == ln.Id));
            var opacity = isTouched ? "1" : "0.35";
            var (border, badge, badgeColor) = nodeDiff?.Change switch
            {
                "added" => ("var(--ds-delta-added)", "+ ADDED", "var(--ds-delta-added)"),
                "removed" => ("var(--ds-delta-removed)", "− REMOVED", "var(--ds-delta-removed)"),
                "moved" => ("var(--ds-delta-moved)", "→ MOVED", "var(--ds-delta-moved)"),
                _ when newCycleNodes.Contains(ln.Id) => ("var(--ds-intent-danger)", "NEW CYCLE", "var(--ds-intent-danger)"),
                _ => ("var(--ds-border-strong)", (string?)null, "var(--ds-text-muted)"),
            };

            var node = diff.Candidate.Nodes.TryGetValue(ln.Id, out var candNode) ? candNode : diff.Baseline.Nodes[ln.Id];
            var subtitle = nodeDiff?.Change == "moved"
                ? $"{nodeDiff.Before?.Layer ?? "none"} → {nodeDiff.After?.Layer ?? "none"}"
                : $"in {node.FanIn} · out {node.FanOut} · I={node.Instability}";
            WriteNodeBoxSvg(svg, ln, border, "var(--ds-surface-raised)", opacity, subtitle, badge, badgeColor);
        }

        svg.Append("</g></svg>");
        return svg.ToString();
    }

    /// <summary>Renders a complete, static-safe HTML review report showing BOTH the System + Diff Overlay and the Current System State.</summary>
    public static string RenderDiffHtml(ArchGraphDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var hasRisk = diff.CyclesAdded.Count > 0
            || diff.EdgeDiffs.Any(e => e.Change == "added" && e.Classification is "cyclic" or "upward-layer");
        var hasWarning = diff.EdgeDiffs.Any(e => e.Change == "added" && e.Classification == "feature-cross")
            || diff.CyclesResized.Any(r => r.AddedMembers.Count > 0);

        var (bandClass, headline) = diff.Status == "unchanged"
            ? ("verdict-neutral", "○ UNCHANGED — Architecture shape did not move")
            : hasRisk
                ? ("verdict-danger", "Δ CHANGED — Structural regressions detected (cycles or upward layer crossings)")
                : hasWarning
                    ? ("verdict-warn", "Δ CHANGED — Cross-feature coupling or cycle shift detected")
                    : ("verdict-neutral", "Δ CHANGED — Architecture shape evolved cleanly");

        var sb = new StringBuilder(16384);
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>")
            .Append(Escape($"Diffra architecture diff — {diff.Candidate.Label}"))
            .Append("</title><style>").Append(HtmlCss).Append("</style></head><body><main>");

        sb.Append("<h1>Architecture diff: ").Append(Escape(diff.Candidate.Label)).Append("</h1>");
        sb.Append("<div class=\"verdict-band ").Append(bandClass).Append("\"><div><div class=\"verdict-title\">")
            .Append(Escape(headline)).Append("</div></div><span class=\"badge\">no policy verdict</span></div>");

        sb.Append("<div class=\"provenance\">");
        if (diff.Candidate.Repo is not null)
        {
            sb.Append("<span>repo: ").Append(Escape(diff.Candidate.Repo)).Append("</span>");
        }

        sb.Append("<span>baseline: ").Append(Escape(ShortCommit(diff.Baseline.Commit ?? diff.Baseline.Id))).Append("</span>");
        sb.Append("<span>candidate: ").Append(Escape(ShortCommit(diff.Candidate.Commit ?? diff.Candidate.Id))).Append("</span>");
        sb.Append("<span>analyzer: ").Append(Escape(diff.Candidate.Analyzer.Tool)).Append(" (").Append(Escape(diff.Candidate.Analyzer.Resolution)).Append(")</span>");
        sb.Append("<span>language: ").Append(Escape(diff.Candidate.Analyzer.Language)).Append("</span>");
        sb.Append("</div>");

        sb.Append("<div class=\"stat-grid\">");
        foreach (var metric in diff.SummaryDeltas.Take(5))
        {
            var label = metric.Name switch
            {
                "cycleCount" => "Cycles ↓",
                "layerViolationCount" => "Layer violations ↓",
                "featureCrossEdgeCount" => "Feature cross-edges ↓",
                "edgeCount" => "Edges",
                "nodeCount" => "Nodes",
                _ => metric.Name,
            };
            var lowerIsBetter = metric.Name is "cycleCount" or "layerViolationCount" or "featureCrossEdgeCount";
            AppendStatCard(sb, label, metric.After, metric.Absolute, lowerIsBetter: lowerIsBetter);
        }

        sb.Append("</div>");

        // Zero-JS CSS tabs: switch between System + Diff Overlay and Current System State.
        sb.Append("<section class=\"view-tabs\"><h2>System map</h2>");
        sb.Append("<input type=\"radio\" name=\"arch-view\" id=\"tab-diff\" checked>");
        sb.Append("<input type=\"radio\" name=\"arch-view\" id=\"tab-current\">");
        sb.Append("<div class=\"tab-bar\"><label for=\"tab-diff\">System + Diff overlay</label><label for=\"tab-current\">Current system state</label></div>");
        sb.Append("<div class=\"legend\">")
            .Append("<span><i class=\"swatch\" style=\"background:var(--ds-delta-added)\"></i>+ added edge</span>")
            .Append("<span><i class=\"swatch\" style=\"background:var(--ds-delta-removed)\"></i>− removed edge</span>")
            .Append("<span><i class=\"swatch\" style=\"background:var(--ds-intent-danger)\"></i>cycle / upward layer</span>")
            .Append("<span><i class=\"swatch\" style=\"background:var(--ds-intent-warning)\"></i>feature-to-feature</span>")
            .Append("<span><i class=\"swatch\" style=\"background:var(--ds-text-muted);opacity:.35\"></i>unchanged system context</span>")
            .Append("</div>");
        sb.Append("<div class=\"tab-panel panel-diff\">").Append(RenderDiffOverlaySvg(diff)).Append("</div>");
        sb.Append("<div class=\"tab-panel panel-current\">").Append(RenderCurrentSvg(diff.Candidate)).Append("</div>");
        sb.Append("</section>");

        // KeyedDiff Table 1: Cycles.
        sb.Append("<section><h2>Cycles</h2><table><caption>Strongly connected components compared by stable cycle ID</caption><thead><tr><th>Change</th><th>Cycle</th><th>Members</th><th class=\"num\">Size</th></tr></thead><tbody>");
        if (diff.CyclesAdded.Count == 0 && diff.CyclesRemoved.Count == 0 && diff.CyclesResized.Count == 0)
        {
            sb.Append("<tr><td colspan=\"4\" class=\"delta-muted\">No cycle changes.</td></tr>");
        }
        else
        {
            foreach (var cycle in diff.CyclesAdded)
            {
                sb.Append("<tr><td class=\"mono intent-danger\">+ added</td><td class=\"mono\">").Append(Escape(cycle.Id))
                    .Append("</td><td class=\"mono\">").Append(Escape(string.Join(" → ", cycle.Members)))
                    .Append("</td><td class=\"num\">").Append(cycle.Members.Count.ToString(CultureInfo.InvariantCulture)).Append("</td></tr>");
            }

            foreach (var resize in diff.CyclesResized)
            {
                sb.Append("<tr><td class=\"mono intent-warning\">Δ resized</td><td class=\"mono\">").Append(Escape(resize.CandidateCycleId))
                    .Append("</td><td class=\"mono\">").Append(Escape(string.Join(" → ", resize.CandidateMembers)))
                    .Append("</td><td class=\"num\">").Append(resize.BaselineMembers.Count.ToString(CultureInfo.InvariantCulture))
                    .Append(" → ").Append(resize.CandidateMembers.Count.ToString(CultureInfo.InvariantCulture)).Append("</td></tr>");
            }

            foreach (var cycle in diff.CyclesRemoved)
            {
                sb.Append("<tr><td class=\"mono delta-added\">− broken</td><td class=\"mono strike\">").Append(Escape(cycle.Id))
                    .Append("</td><td class=\"mono strike\">").Append(Escape(string.Join(" → ", cycle.Members)))
                    .Append("</td><td class=\"num\">").Append(cycle.Members.Count.ToString(CultureInfo.InvariantCulture)).Append("</td></tr>");
            }
        }

        sb.Append("</tbody></table></section>");

        // KeyedDiff Table 2: Edges.
        sb.Append("<section><h2>Edges</h2><table><caption>Module dependencies compared by canonical edge ID</caption><thead><tr><th>Change</th><th>Edge</th><th>Class</th><th class=\"num\">Refs</th><th>Evidence</th></tr></thead><tbody>");
        if (diff.EdgeDiffs.Count == 0)
        {
            sb.Append("<tr><td colspan=\"5\" class=\"delta-muted\">No edge changes.</td></tr>");
        }
        else
        {
            foreach (var edge in diff.EdgeDiffs)
            {
                var (glyph, cls) = ChangePresentation(edge.Change);
                var badgeCls = edge.Classification switch
                {
                    "cyclic" or "upward-layer" => "badge badge-danger",
                    "feature-cross" => "badge badge-warn",
                    _ => "badge",
                };
                var refs = edge.Change switch
                {
                    "added" => $"+{edge.After!.Weight}",
                    "removed" => $"−{edge.Before!.Weight}",
                    _ => $"{edge.Before!.Weight} → {edge.After!.Weight}",
                };
                var sampleEdge = edge.After ?? edge.Before;
                var ev = sampleEdge is { Evidence.Count: > 0 } ? sampleEdge.Evidence[0].FormatCompact() : "—";
                sb.Append("<tr><td class=\"mono ").Append(cls).Append("\">").Append(glyph).Append(' ').Append(Escape(edge.Change))
                    .Append("</td><td class=\"mono").Append(edge.Change == "removed" ? " strike" : "").Append("\">").Append(Escape(edge.EdgeId))
                    .Append("</td><td><span class=\"").Append(badgeCls).Append("\">").Append(Escape(edge.Classification))
                    .Append("</span></td><td class=\"num mono\">").Append(Escape(refs))
                    .Append("</td><td class=\"mono\">").Append(Escape(ev)).Append("</td></tr>");
            }
        }

        sb.Append("</tbody></table></section>");

        // KeyedDiff Table 3: Nodes.
        sb.Append("<section><h2>Modules</h2><table><caption>Nodes compared by canonical module ID</caption><thead><tr><th>Change</th><th>Module</th><th>Layer</th><th class=\"num\">Fan-in</th><th class=\"num\">Fan-out</th><th class=\"num\">Instability</th></tr></thead><tbody>");
        if (diff.NodeDiffs.Count == 0)
        {
            sb.Append("<tr><td colspan=\"6\" class=\"delta-muted\">No module changes.</td></tr>");
        }
        else
        {
            foreach (var node in diff.NodeDiffs)
            {
                var (glyph, cls) = ChangePresentation(node.Change);
                var layerText = node.Change == "moved"
                    ? $"{node.Before?.Layer ?? "unassigned"} → {node.After?.Layer ?? "unassigned"}"
                    : (node.After ?? node.Before)?.Layer ?? "unassigned";
                var fanIn = FormatIntTransition(node.Before?.FanIn, node.After?.FanIn);
                var fanOut = FormatIntTransition(node.Before?.FanOut, node.After?.FanOut);
                var inst = node.Before is not null && node.After is not null && node.Before.Instability != node.After.Instability
                    ? $"{node.Before.Instability} → {node.After.Instability}"
                    : (node.After ?? node.Before)!.Instability;
                sb.Append("<tr><td class=\"mono ").Append(cls).Append("\">").Append(glyph).Append(' ').Append(Escape(node.Change))
                    .Append("</td><td class=\"mono").Append(node.Change == "removed" ? " strike" : "").Append("\">").Append(Escape(node.NodeId))
                    .Append("</td><td class=\"mono\">").Append(Escape(layerText))
                    .Append("</td><td class=\"num mono\">").Append(Escape(fanIn))
                    .Append("</td><td class=\"num mono\">").Append(Escape(fanOut))
                    .Append("</td><td class=\"num mono\">").Append(Escape(inst)).Append("</td></tr>");
            }
        }

        sb.Append("</tbody></table></section></main></body></html>");
        return sb.ToString();
    }

    /// <summary>Renders a full human-readable Markdown report of an architecture diff.</summary>
    public static string RenderDiffMarkdown(ArchGraphDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var sb = new StringBuilder();
        sb.Append("# Architecture diff: ").Append(diff.Candidate.Label).Append("\n\n");
        sb.Append("- Status: `").Append(diff.Status).Append("`\n");
        sb.Append("- Analyzer: `").Append(diff.Candidate.Analyzer.Tool).Append("` (`").Append(diff.Candidate.Analyzer.Resolution).Append("`)\n");
        sb.Append("- Language: `").Append(diff.Candidate.Analyzer.Language).Append("`\n\n");

        sb.Append("## Summary\n\n| Metric | Before | After | Delta |\n| --- | ---: | ---: | ---: |\n");
        foreach (var m in diff.SummaryDeltas)
        {
            var delta = m.Absolute > 0 ? $"+{m.Absolute}" : m.Absolute.ToString(CultureInfo.InvariantCulture);
            sb.Append("| `").Append(m.Name).Append("` | ").Append(m.Before).Append(" | ").Append(m.After).Append(" | ").Append(delta).Append(" |\n");
        }

        if (diff.CyclesAdded.Count > 0 || diff.CyclesRemoved.Count > 0 || diff.CyclesResized.Count > 0)
        {
            sb.Append("\n## Cycles\n\n| Change | Cycle | Members |\n| --- | --- | --- |\n");
            foreach (var c in diff.CyclesAdded)
            {
                sb.Append("| `+ added` | `").Append(c.Id).Append("` | `").Append(string.Join(" -> ", c.Members)).Append("` |\n");
            }

            foreach (var r in diff.CyclesResized)
            {
                sb.Append("| `Δ resized` | `").Append(r.CandidateCycleId).Append("` | `").Append(string.Join(" -> ", r.CandidateMembers)).Append("` |\n");
            }

            foreach (var c in diff.CyclesRemoved)
            {
                sb.Append("| `- broken` | `").Append(c.Id).Append("` | `").Append(string.Join(" -> ", c.Members)).Append("` |\n");
            }
        }

        if (diff.EdgeDiffs.Count > 0)
        {
            sb.Append("\n## Edges\n\n| Change | Edge | Class | Refs | Evidence |\n| --- | --- | --- | ---: | --- |\n");
            foreach (var e in diff.EdgeDiffs)
            {
                var refs = e.Change switch
                {
                    "added" => $"+{e.After!.Weight}",
                    "removed" => $"-{e.Before!.Weight}",
                    _ => $"{e.Before!.Weight} -> {e.After!.Weight}",
                };
                var sampleEdge = e.After ?? e.Before;
                var ev = sampleEdge is { Evidence.Count: > 0 } ? sampleEdge.Evidence[0].FormatCompact() : "-";
                sb.Append("| `").Append(e.Change).Append("` | `").Append(e.EdgeId).Append("` | `").Append(e.Classification)
                    .Append("` | `").Append(refs).Append("` | `").Append(ev).Append("` |\n");
            }
        }

        return sb.ToString();
    }

    /// <summary>Renders a one-screen PR comment headed by attention level; empty when unchanged.</summary>
    public static string RenderDiffComment(ArchGraphDiff diff, OutputLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(diff);
        limits ??= OutputLimits.Default;
        if (diff.Status == "unchanged")
        {
            return string.Empty;
        }

        var level = ComputeAttention(diff);
        var rows = BuildHighlightRows(diff);
        var visible = Math.Min(limits.Findings, rows.Count);
        var sb = new StringBuilder();
        sb.Append("### ").Append(Attention.Label(level)).Append(" — Architecture diff (`").Append(diff.Candidate.Id).Append("`)\n\n");
        sb.Append("| Change | Item | Detail |\n| --- | --- | --- |\n");
        foreach (var row in rows.Take(visible))
        {
            sb.Append("| `").Append(row.Change).Append("` | `").Append(row.Id).Append("` | ").Append(row.Detail).Append(" |\n");
        }

        if (visible < rows.Count)
        {
            var hidden = rows.Count - visible;
            sb.Append('\n').Append(hidden).Append(hidden == 1 ? " more structural change is" : " more structural changes are").Append(" not shown.\n");
            sb.Append("<details><summary>All ").Append(rows.Count).Append(" structural changes</summary>\n\n");
            sb.Append("| Change | Item | Detail |\n| --- | --- | --- |\n");
            foreach (var row in rows)
            {
                sb.Append("| `").Append(row.Change).Append("` | `").Append(row.Id).Append("` | ").Append(row.Detail).Append(" |\n");
            }

            sb.Append("\n</details>\n");
        }

        return sb.ToString();
    }

    /// <summary>Renders token-budgeted agent-facing Markdown with exact symbol/file evidence for refactoring.</summary>
    public static string RenderDiffAgent(ArchGraphDiff diff, OutputLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(diff);
        limits ??= OutputLimits.Default;
        var level = ComputeAttention(diff);
        var rows = BuildHighlightRows(diff);
        var sb = new StringBuilder();
        sb.Append("attention: ").Append(Attention.Token(level)).Append('\n');
        sb.Append("status: ").Append(diff.Status).Append('\n');
        sb.Append("cycles: ").Append(diff.Baseline.Summary.CycleCount).Append("->").Append(diff.Candidate.Summary.CycleCount).Append('\n');
        sb.Append("layer_violations: ").Append(diff.Baseline.Summary.LayerViolationCount).Append("->").Append(diff.Candidate.Summary.LayerViolationCount).Append('\n');
        sb.Append("feature_cross_edges: ").Append(diff.Baseline.Summary.FeatureCrossEdgeCount).Append("->").Append(diff.Candidate.Summary.FeatureCrossEdgeCount).Append('\n');
        sb.Append("changes: ").Append(rows.Count).Append('\n');

        var budget = Math.Max(200, (limits.AgentTokens - 20) * 4);
        var written = 0;
        foreach (var row in rows)
        {
            var line = $"- {row.Change} {row.Id}: {row.Detail}\n";
            if (sb.Length + line.Length > budget)
            {
                break;
            }

            sb.Append(line);
            written++;
        }

        if (written < rows.Count)
        {
            sb.Append("truncated: ").Append(rows.Count - written).Append(" more changes in full report\n");
        }

        return sb.ToString();
    }

    private static AttentionLevel ComputeAttention(ArchGraphDiff diff)
    {
        if (diff.Status == "unchanged")
        {
            return AttentionLevel.Quiet;
        }

        if (diff.CyclesAdded.Count > 0
            || diff.EdgeDiffs.Any(e => e.Change == "added" && e.Classification is "cyclic" or "upward-layer"))
        {
            return AttentionLevel.Act;
        }

        return AttentionLevel.Review;
    }

    private static List<(string Change, string Id, string Detail)> BuildHighlightRows(ArchGraphDiff diff)
    {
        var rows = new List<(string Change, string Id, string Detail)>();
        foreach (var cycle in diff.CyclesAdded)
        {
            rows.Add(("cycle-added", cycle.Id, string.Join(" -> ", cycle.Members)));
        }

        foreach (var resize in diff.CyclesResized)
        {
            rows.Add(("cycle-resized", resize.CandidateCycleId, $"+[{string.Join(", ", resize.AddedMembers)}] -[{string.Join(", ", resize.RemovedMembers)}]"));
        }

        foreach (var cycle in diff.CyclesRemoved)
        {
            rows.Add(("cycle-broken", cycle.Id, string.Join(" -> ", cycle.Members)));
        }

        foreach (var edge in diff.EdgeDiffs)
        {
            var sampleEdge = edge.After ?? edge.Before;
            var ev = sampleEdge is { Evidence.Count: > 0 } ? sampleEdge.Evidence[0].FormatCompact() : null;
            var detail = ev is null ? $"[{edge.Classification}]" : $"[{edge.Classification}] {ev}";
            rows.Add(($"edge-{edge.Change}", edge.EdgeId, detail));
        }

        foreach (var node in diff.NodeDiffs.Where(n => n.Change is "added" or "removed" or "moved"))
        {
            var detail = node.Change == "moved"
                ? $"layer {node.Before?.Layer ?? "unassigned"} -> {node.After?.Layer ?? "unassigned"}"
                : $"layer {(node.After ?? node.Before)?.Layer ?? "unassigned"}";
            rows.Add(($"node-{node.Change}", node.NodeId, detail));
        }

        return rows;
    }

    private static void BeginSvg(StringBuilder svg, ArchGraphLayoutResult layout, string accessibleTitle)
    {
        svg.Append(string.Create(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {layout.Width:0.#} {layout.Height:0.#}\" width=\"{layout.Width:0.#}\" height=\"{layout.Height:0.#}\" role=\"img\" aria-label=\"{Escape(accessibleTitle)}\">"));
        svg.Append("<defs>");
        WriteArrowMarker(svg, "arrow-muted", "var(--ds-text-muted)");
        WriteArrowMarker(svg, "arrow-danger", "var(--ds-intent-danger)");
        WriteArrowMarker(svg, "arrow-warn", "var(--ds-intent-warning)");
        WriteArrowMarker(svg, "arrow-added", "var(--ds-delta-added)");
        WriteArrowMarker(svg, "arrow-removed", "var(--ds-delta-removed)");
        WriteArrowMarker(svg, "arrow-moved", "var(--ds-delta-moved)");
        svg.Append("</defs>");
    }

    private static void WriteArrowMarker(StringBuilder svg, string id, string fill) =>
        svg.Append("<marker id=\"").Append(id).Append("\" viewBox=\"0 0 10 10\" refX=\"8.5\" refY=\"5\" markerWidth=\"6\" markerHeight=\"6\" orient=\"auto-start-reverse\"><path d=\"M 0 1 L 10 5 L 0 9 z\" fill=\"")
            .Append(fill).Append("\"/></marker>");

    private static void RenderBandsSvg(StringBuilder svg, ArchGraphLayoutResult layout)
    {
        svg.Append("<g data-layer=\"bands\">");
        foreach (var band in layout.Bands)
        {
            svg.Append(string.Create(CultureInfo.InvariantCulture,
                $"<rect x=\"{band.X:0.#}\" y=\"{band.Y:0.#}\" width=\"{band.Width:0.#}\" height=\"{band.Height:0.#}\" rx=\"6\" fill=\"var(--ds-surface-base)\" stroke=\"var(--ds-border-default)\" stroke-width=\"1\"/>"));
            svg.Append(string.Create(CultureInfo.InvariantCulture,
                $"<text x=\"{band.X + 10:0.#}\" y=\"{band.Y + 17:0.#}\" fill=\"var(--ds-text-muted)\" font-family=\"ui-monospace,SFMono-Regular,Consolas,monospace\" font-size=\"10\" font-weight=\"700\">{Escape(band.Label.ToUpperInvariant())}</text>"));
        }

        svg.Append("</g>");
    }

    private static void WriteEdgePathSvg(StringBuilder svg, string d, string stroke, string marker, string dash, string width, string opacity, string title)
    {
        svg.Append("<path d=\"").Append(d).Append("\" fill=\"none\" stroke=\"").Append(stroke).Append("\" stroke-width=\"").Append(width)
            .Append("\" stroke-opacity=\"").Append(opacity).Append("\" marker-end=\"url(#").Append(marker).Append(")\"");
        if (dash.Length > 0)
        {
            svg.Append(" stroke-dasharray=\"").Append(dash).Append('"');
        }

        svg.Append("><title>").Append(Escape(title)).Append("</title></path>");
    }

    private static void WriteNodeBoxSvg(StringBuilder svg, ArchLayoutNode ln, string border, string fill, string opacity, string subtitle, string? badge, string badgeColor)
    {
        svg.Append("<g opacity=\"").Append(opacity).Append("\">");
        svg.Append(string.Create(CultureInfo.InvariantCulture,
            $"<rect x=\"{ln.X:0.#}\" y=\"{ln.Y:0.#}\" width=\"{ln.Width:0.#}\" height=\"{ln.Height:0.#}\" rx=\"4\" fill=\"{fill}\" stroke=\"{border}\" stroke-width=\"1.8\"><title>{Escape(ln.Id)}</title></rect>"));
        svg.Append(string.Create(CultureInfo.InvariantCulture,
            $"<text x=\"{ln.X + 10:0.#}\" y=\"{ln.Y + 22:0.#}\" fill=\"var(--ds-text-primary)\" font-family=\"ui-monospace,SFMono-Regular,Consolas,monospace\" font-size=\"12\" font-weight=\"700\">{Escape(Truncate(ln.Label, 21))}</text>"));
        svg.Append(string.Create(CultureInfo.InvariantCulture,
            $"<text x=\"{ln.X + 10:0.#}\" y=\"{ln.Y + 39:0.#}\" fill=\"var(--ds-text-muted)\" font-family=\"ui-monospace,SFMono-Regular,Consolas,monospace\" font-size=\"9.5\">{Escape(Truncate(subtitle, 26))}</text>"));
        if (badge is not null)
        {
            svg.Append(string.Create(CultureInfo.InvariantCulture,
                $"<text x=\"{ln.X + ln.Width - 8:0.#}\" y=\"{ln.Y + 13:0.#}\" text-anchor=\"end\" fill=\"{badgeColor}\" font-family=\"ui-monospace,SFMono-Regular,Consolas,monospace\" font-size=\"8.5\" font-weight=\"800\">{Escape(badge)}</text>"));
        }

        svg.Append("</g>");
    }

    private static void AppendStatCard(StringBuilder sb, string label, int value, int? delta, bool lowerIsBetter = false, bool dangerWhenPositive = false, bool warnWhenPositive = false)
    {
        sb.Append("<div class=\"stat-card\"><div class=\"stat-label\">").Append(Escape(label)).Append("</div><div class=\"stat-value");
        if (dangerWhenPositive && value > 0)
        {
            sb.Append(" intent-danger");
        }
        else if (warnWhenPositive && value > 0)
        {
            sb.Append(" intent-warning");
        }

        sb.Append("\">").Append(value.ToString(CultureInfo.InvariantCulture));
        if (delta is { } d && d != 0)
        {
            var sign = d > 0 ? "+" : "−";
            var cls = lowerIsBetter
                ? (d > 0 ? "intent-danger" : "intent-success")
                : (d > 0 ? "delta-added" : "delta-removed");
            sb.Append("<span class=\"stat-delta ").Append(cls).Append("\">").Append(sign).Append(Math.Abs(d).ToString(CultureInfo.InvariantCulture)).Append("</span>");
        }

        sb.Append("</div></div>");
    }

    private static (string Glyph, string CssClass) ChangePresentation(string change) => change switch
    {
        "added" => ("+", "delta-added"),
        "removed" => ("−", "delta-removed"),
        "moved" => ("→", "delta-moved"),
        _ => ("Δ", "delta-muted"),
    };

    private static string FormatIntTransition(int? before, int? after) => (before, after) switch
    {
        (null, { } a) => $"+{a}",
        ({ } b, null) => $"−{b}",
        ({ } b, { } a) when b != a => $"{b} → {a}",
        ({ } b, _) => b.ToString(CultureInfo.InvariantCulture),
        _ => "—",
    };

    private static bool IsFeatureCross(ArchEdge edge, ArchGraphDocument graph) =>
        graph.Nodes.TryGetValue(edge.Source, out var src)
        && graph.Nodes.TryGetValue(edge.Target, out var dst)
        && string.Equals(src.Layer, "features", StringComparison.Ordinal)
        && string.Equals(dst.Layer, "features", StringComparison.Ordinal);

    private static string ShortCommit(string value) => value.Length > 10 ? value[..10] : value;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    private static string Escape(string value) => WebUtility.HtmlEncode(value);
}
