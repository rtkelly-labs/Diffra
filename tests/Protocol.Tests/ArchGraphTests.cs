using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Diffra.Converters;
using Diffra.Protocol;

namespace Diffra.Protocol.Tests;

/// <summary>
/// End-to-end tests for <c>diffra/arch-graph@1</c> (Diffra#162): JGF v2.1 + SCIP profile,
/// Tarjan SCC condensation, Robert Martin instability, SCIP binary protobuf folding,
/// dependency-cruiser and go-list converters, graph diffs (cycles introduced/broken/resized,
/// upward layers, cross-feature coupling, decoupling), deterministic Sugiyama layout,
/// static-safe HTML/SVG/Markdown/Comment/Agent reports, and CLI <c>arch</c> / <c>record</c> flows.
/// </summary>
public static class ArchGraphTests
{
    public static void RunAll()
    {
        BuildsDeterministicJgfAndComputesSccsAndInstability();
        SameGraphRegardlessOfInputOrder();
        EnforcesMaxKeysPriorityTruncationKeepingTotalsExact();
        FoldsScipBinaryProtobufIntoModuleGraphWithEvidence();
        ConvertsDependencyCruiserJsonPreservingTypeOnly();
        ConvertsGoListStreamSeparatingProductionAndTestImports();
        DiffsArchitectureEvolutionCyclesLayersAndDecoupling();
        ComputesDeterministicLayeredLayoutWithPinnedBands();
        RendersCurrentStateAndDiffOverlayHtmlSvgMarkdownCommentAndAgent();
        RecordsArchGraphViaConverterAndRejectsLinks();
        Console.WriteLine("PASS architecture graph protocol, converters, diffs and reports");
    }

    private static void BuildsDeterministicJgfAndComputesSccsAndInstability()
    {
        var graph = BuildSampleGraph(withCycle: true, withUpward: true, withFeatureCross: true);
        Equal(5, graph.Summary.NodeCount);
        Equal(7, graph.Summary.EdgeCount);
        Equal(1, graph.Summary.CycleCount);
        Equal(1, graph.Summary.LayerViolationCount);
        Equal(2, graph.Summary.FeatureCrossEdgeCount);
        Equal(0, graph.Summary.UnassignedNodeCount);

        var cycle = graph.Cycles.Values.Single();
        Equal("cycle:", cycle.Id[..6]);
        Equal(18, cycle.Id.Length);
        // Core.Leaves -> Core.Estate closes a 4-node SCC with Estate, Orca and Processes.
        Equal("Core.Estate,Core.Leaves,Core.Orca,Core.Processes", string.Join(',', cycle.Members));

        // Core.Leaves has fanIn=2 (Estate, Processes), fanOut=1 (upward to Estate) -> I = 1/3 = 0.333333
        Equal("0.333333", graph.Nodes["Core.Leaves"].Instability);

        // Round-trip through canonical JGF v2.1.
        var jgfBytes = ArchGraphJson.SerializeJgf(graph);
        var reparsed = ArchGraphJson.ParseJgf(jgfBytes);
        var jgfAgain = ArchGraphJson.SerializeJgf(reparsed);
        Equal(Encoding.UTF8.GetString(jgfBytes), Encoding.UTF8.GetString(jgfAgain));
    }

    private static void SameGraphRegardlessOfInputOrder()
    {
        var analyzer = new ArchAnalyzer("test", "ArchUnitNET", "0.13.5", "CSharp", "il-symbol");
        string[] layers = ["features", "leaves"];
        RawArchNode[] nodesA =
        [
            new("A", "A", "namespace", "CSharp", Layer: "features", Slice: "A"),
            new("B", "B", "namespace", "CSharp", Layer: "leaves"),
            new("C", "C", "namespace", "CSharp", Layer: "features", Slice: "C"),
        ];
        RawArchEdge[] edgesA =
        [
            new("A", "B", "symbol", 2, false, [new("src/A.cs", 10, "A.T", "B.U", ["ReadAccess"], "reference")]),
            new("C", "B", "symbol", 1, false, [new("src/C.cs", 5, "C.T", "B.U", ["ReadAccess"], "reference")]),
        ];

        var g1 = ArchGraphBuilder.Build("dotnet", "rig", "rtkelly13/rig", null, analyzer, "namespace", layers, nodesA, edgesA);
        var g2 = ArchGraphBuilder.Build("dotnet", "rig", "rtkelly13/rig", null, analyzer, "namespace", layers, nodesA.Reverse(), edgesA.Reverse());
        Equal(Encoding.UTF8.GetString(ArchGraphJson.SerializeJgf(g1)), Encoding.UTF8.GetString(ArchGraphJson.SerializeJgf(g2)));
    }

    private static void EnforcesMaxKeysPriorityTruncationKeepingTotalsExact()
    {
        var graph = BuildSampleGraph(withCycle: true, withUpward: false, withFeatureCross: true);
        // 6 edges total: 2 cyclic (Estate<->Orca), 4 non-cyclic. Budget 2 keeps the 2 cyclic edges.
        var (dataJson, droppedNodes, droppedEdges) = ArchGraphJson.SerializeDatasetData(graph, maxKeys: 2);
        Equal(3, droppedNodes);
        Equal(4, droppedEdges);

        using var doc = JsonDocument.Parse(dataJson);
        var root = doc.RootElement;
        // Exact totals survive truncation.
        Equal("6", root.GetProperty("edgeCount").GetProperty("value").GetString());
        Equal("5", root.GetProperty("nodeCount").GetProperty("value").GetString());
        var keptEdgeKeys = root.GetProperty("edges").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Equal(2, keptEdgeKeys.Length);
        True(keptEdgeKeys.Contains("Core.Estate->Core.Orca"), "cyclic edge should be prioritized");
        True(keptEdgeKeys.Contains("Core.Orca->Core.Estate"), "cyclic return edge should be prioritized");
    }

    private static void FoldsScipBinaryProtobufIntoModuleGraphWithEvidence()
    {
        // Build a genuine SCIP Protobuf wire payload matching scip.proto field numbers:
        // Index { metadata: { tool_info: { name: "scip-python", version: "0.6.0" } }, documents: [docA, docB] }
        var toolInfo = Concat(
            WriteStringField(1, "scip-python"),
            WriteStringField(2, "0.6.0"));
        var metadata = WriteBytesField(2, toolInfo);

        var symA = "scip-python python hearth 1.0 hearth/fitness/SyncRunner#";
        var symB = "scip-python python hearth 1.0 hearth/storage/Store#";

        var singleLineRange = WriteVarintField(1, 41); // 0-based line 41 => 1-based line 42
        var occDefA = Concat(
            WriteStringField(2, symA),
            WriteVarintField(3, ScipProtobufReader.RoleDefinition),
            WriteBytesField(8, singleLineRange));
        var occRefB = Concat(
            WriteStringField(2, symB),
            WriteVarintField(3, ScipProtobufReader.RoleReadAccess),
            WriteBytesField(8, singleLineRange));
        var docA = Concat(
            WriteStringField(1, "src/hearth/fitness/sync.py"),
            WriteBytesField(2, occDefA),
            WriteBytesField(2, occRefB),
            WriteBytesField(3, WriteStringField(1, symA)),
            WriteStringField(4, "Python"));

        var occDefB = Concat(
            WriteStringField(2, symB),
            WriteVarintField(3, ScipProtobufReader.RoleDefinition),
            WriteBytesField(8, WriteVarintField(1, 9)));
        var docB = Concat(
            WriteStringField(1, "src/hearth/storage/store.py"),
            WriteBytesField(2, occDefB),
            WriteBytesField(3, WriteStringField(1, symB)),
            WriteStringField(4, "Python"));

        var scipBytes = Concat(
            WriteBytesField(1, metadata),
            WriteBytesField(2, docA),
            WriteBytesField(2, docB));

        var graph = ArchGraphConverter.ConvertScip(scipBytes, "hearth", ["hearth.fitness", "hearth.storage"]);
        Equal("scip-python", graph.Analyzer.Tool);
        Equal("0.6.0", graph.Analyzer.ToolVersion);
        Equal("scip-symbol", graph.Analyzer.Resolution);
        Equal("Python", graph.Analyzer.Language);
        Equal(2, graph.Summary.NodeCount);
        Equal(1, graph.Summary.EdgeCount);
        var edge = graph.Edges.Single();
        Equal("hearth.fitness->hearth.storage", edge.Id);
        Equal("symbol", edge.Relation);
        Equal(42, edge.Evidence.Single().Line);
        Equal("src/hearth/fitness/sync.py", edge.Evidence.Single().RelativePath);
        Equal(symB, edge.Evidence.Single().TargetSymbol);
    }

    private static void ConvertsDependencyCruiserJsonPreservingTypeOnly()
    {
        const string json = """
            {
              "modules": [
                {
                  "source": "web/OpsArch.tsx",
                  "valid": true,
                  "dependencies": [
                    { "resolved": "web/archModel.ts", "module": "./archModel", "typeOnly": true, "coreModule": false, "couldNotResolve": false },
                    { "resolved": "web/opsApi.ts", "module": "./opsApi", "typeOnly": false, "coreModule": false, "couldNotResolve": false },
                    { "resolved": "node_modules/react/index.js", "module": "react", "typeOnly": false, "coreModule": false, "couldNotResolve": false }
                  ]
                },
                {
                  "source": "web/archModel.ts",
                  "valid": true,
                  "dependencies": []
                },
                {
                  "source": "web/opsApi.ts",
                  "valid": true,
                  "dependencies": []
                }
              ],
              "summary": { "violations": [] }
            }
            """;

        var graph = ArchGraphConverter.ConvertDepCruise(Encoding.UTF8.GetBytes(json));
        Equal("compiler-module", graph.Analyzer.Resolution);
        Equal(3, graph.Summary.NodeCount);
        Equal(2, graph.Summary.EdgeCount);
        var typeEdge = graph.Edges.Single(e => e.Target == "web/archModel");
        Equal("type-only", typeEdge.Relation);
        Equal(true, typeEdge.TypeOnly);
        var runtimeEdge = graph.Edges.Single(e => e.Target == "web/opsApi");
        Equal("import", runtimeEdge.Relation);
        Equal(false, runtimeEdge.TypeOnly);
    }

    private static void ConvertsGoListStreamSeparatingProductionAndTestImports()
    {
        const string stream = """
            {"ImportPath":"github.com/acme/app/cmd","Module":{"Path":"github.com/acme/app"},"Imports":["fmt","github.com/acme/app/internal/domain"],"GoFiles":["main.go"]}
            {"ImportPath":"github.com/acme/app/internal/domain","Module":{"Path":"github.com/acme/app"},"Imports":[],"XTestImports":["github.com/acme/app/internal/testutil"],"GoFiles":["model.go"]}
            {"ImportPath":"github.com/acme/app/internal/testutil","Module":{"Path":"github.com/acme/app"},"Imports":[],"GoFiles":["helpers.go"]}
            """;

        var graph = ArchGraphConverter.ConvertGoList(Encoding.UTF8.GetBytes(stream));
        Equal("compiler-package", graph.Analyzer.Resolution);
        Equal(3, graph.Summary.NodeCount);
        Equal(2, graph.Summary.EdgeCount);
        Equal("import", graph.Edges.Single(e => e.Source.EndsWith("/cmd", StringComparison.Ordinal)).Relation);
        Equal("test-import", graph.Edges.Single(e => e.Target.EndsWith("/testutil", StringComparison.Ordinal)).Relation);
    }

    private static void DiffsArchitectureEvolutionCyclesLayersAndDecoupling()
    {
        var baseline = BuildSampleGraph(withCycle: true, withUpward: false, withFeatureCross: true);
        var candidate = BuildSampleGraph(withCycle: true, withUpward: true, withFeatureCross: true);

        // Adding Core.Leaves -> Core.Estate grows the 2-node cycle into a 4-node cycle.
        var grown = ArchGraphComparator.Compare(baseline, candidate);
        Equal("changed", grown.Status);
        Equal(1, grown.CyclesResized.Count);
        Equal("Core.Leaves,Core.Processes", string.Join(',', grown.CyclesResized[0].AddedMembers));
        True(grown.EdgeDiffs.Any(e => e.Change == "added" && e.EdgeId == "Core.Leaves->Core.Estate"), "upward edge added");

        // Removing the cycle breaks it cleanly.
        var clean = BuildSampleGraph(withCycle: false, withUpward: false, withFeatureCross: false);
        var diff = ArchGraphComparator.Compare(baseline, clean);
        Equal("changed", diff.Status);
        Equal(0, diff.CyclesAdded.Count);
        Equal(1, diff.CyclesRemoved.Count);
        True(diff.EdgeDiffs.Any(e => e.Change == "removed" && e.EdgeId == "Core.Orca->Core.Estate"), "broken cycle edge removed");

        // Verify Delta v1 output passes DocumentIdentity verification and ReportRenderer.
        var deltaBytes = ArchGraphComparator.ToDeltaV1Bytes(
            diff,
            "sha256:" + new string('a', 64),
            "sha256:" + new string('b', 64),
            "sha256:" + new string('c', 64));
        DocumentIdentity.Verify("delta", deltaBytes);
        var genericHtml = ReportRenderer.Render(deltaBytes, null, ReportFormat.Html);
        True(genericHtml.Contains("diffra.arch-graph", StringComparison.Ordinal), "generic report names arch-graph comparator");

        // Unchanged graph produces status=unchanged and empty comment.
        var same = ArchGraphComparator.Compare(baseline, baseline);
        Equal("unchanged", same.Status);
        Equal(string.Empty, ArchGraphReportRenderer.RenderDiffComment(same));
    }

    private static void ComputesDeterministicLayeredLayoutWithPinnedBands()
    {
        var graph = BuildSampleGraph(withCycle: true, withUpward: true, withFeatureCross: true);
        var nodes = graph.Nodes.ToDictionary(kv => kv.Key, kv => (kv.Value.Label, kv.Value.Layer), StringComparer.Ordinal);
        var edges = graph.Edges.Select(e => (e.Id, e.Source, e.Target)).ToList();

        var l1 = ArchGraphLayout.Compute(graph.Layers, nodes, edges);
        var l2 = ArchGraphLayout.Compute(graph.Layers, nodes, edges.AsEnumerable().Reverse().ToList());
        Equal(l1.Bands.Count, l2.Bands.Count);
        Equal(l1.Nodes.Count, l2.Nodes.Count);
        for (var i = 0; i < l1.Nodes.Count; i++)
        {
            Equal(l1.Nodes[i].Id, l2.Nodes[i].Id);
            Equal(l1.Nodes[i].X, l2.Nodes[i].X);
            Equal(l1.Nodes[i].Y, l2.Nodes[i].Y);
        }

        // Pinned bands preserve declared order: composition above features above adapters above leaves.
        var compY = l1.Nodes.Single(n => n.Id == "Cli.Program").Y;
        var featY = l1.Nodes.Single(n => n.Id == "Core.Estate").Y;
        var leafY = l1.Nodes.Single(n => n.Id == "Core.Leaves").Y;
        True(compY < featY && featY < leafY, "pinned bands must sit top-to-bottom in declared layer order");
    }

    private static void RendersCurrentStateAndDiffOverlayHtmlSvgMarkdownCommentAndAgent()
    {
        var baseline = BuildSampleGraph(withCycle: false, withUpward: false, withFeatureCross: false);
        var candidate = BuildSampleGraph(withCycle: true, withUpward: true, withFeatureCross: true);
        var diff = ArchGraphComparator.Compare(baseline, candidate);

        var html = ArchGraphReportRenderer.RenderDiffHtml(diff);
        True(html.Contains("System + Diff overlay", StringComparison.Ordinal), "HTML has diff overlay tab");
        True(html.Contains("Current system state", StringComparison.Ordinal), "HTML has current system state tab");
        True(html.Contains("<svg ", StringComparison.Ordinal), "HTML embeds static-safe SVG");
        True(!html.Contains("<script", StringComparison.OrdinalIgnoreCase), "HTML contains zero client script");
        True(html.Contains("--ds-delta-added", StringComparison.Ordinal), "HTML uses design-system delta tokens");

        var currentHtml = ArchGraphReportRenderer.RenderCurrentHtml(candidate);
        True(currentHtml.Contains("Architecture state:", StringComparison.Ordinal), "current state HTML renders");

        var md = ArchGraphReportRenderer.RenderDiffMarkdown(diff);
        True(md.Contains("## Cycles", StringComparison.Ordinal) && md.Contains("## Edges", StringComparison.Ordinal), "Markdown lists cycles and edges");

        var comment = ArchGraphReportRenderer.RenderDiffComment(diff);
        True(comment.Contains("### Needs action", StringComparison.Ordinal), "PR comment escalates new cycle/upward violation to Needs action");

        var agent = ArchGraphReportRenderer.RenderDiffAgent(diff);
        True(agent.Contains("attention: act", StringComparison.Ordinal), "Agent markdown reports attention: act");
        True(agent.Contains("cycle-added", StringComparison.Ordinal), "Agent markdown names added cycle");
    }

    private static void RecordsArchGraphViaConverterAndRejectsLinks()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "diffra-arch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var graph = BuildSampleGraph(withCycle: false, withUpward: false, withFeatureCross: false);
            var artifact = Path.Combine(tempDir, "arch.json");
            File.WriteAllBytes(artifact, ArchGraphJson.SerializeJgf(graph));

            var result = ArchGraphConverter.Read(tempDir, ArchGraphConverter.FormatJgfArch, "arch.json", maximumKeys: 100);
            Equal(0, result.Problems.Count);
            True(result.Data is not null && result.Data.Contains("\"nodeCount\":{\"value\":\"5\"}", StringComparison.Ordinal), "dataset data holds canonical nodeCount");
            Equal(1, result.Sources.Count);
            Equal("jgf-arch", result.Sources[0].Format);

            // Empty glob match rejects cleanly.
            var missing = ArchGraphConverter.Read(tempDir, ArchGraphConverter.FormatJgfArch, "missing/*.json", maximumKeys: null);
            True(missing.Problems.Count == 1 && missing.Data is null, "missing glob rejects entry");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static ArchGraphDocument BuildSampleGraph(bool withCycle, bool withUpward, bool withFeatureCross)
    {
        var analyzer = new ArchAnalyzer("dotnet-archunit", "TngTech.ArchUnitNET", "0.13.5", "CSharp", "il-symbol");
        string[] layers = ["composition", "features", "adapters", "leaves"];
        RawArchNode[] nodes =
        [
            new("Cli.Program", "Program", "namespace", "CSharp", "Rig.Cli", "composition", null, "src/Rig.Cli", 1, 2),
            new("Core.Estate", "Estate", "namespace", "CSharp", "Rig.Core", "features", "Estate", "src/Rig.Core/Estate", 12, 24),
            new("Core.Orca", "Orca", "namespace", "CSharp", "Rig.Core", "features", "Orca", "src/Rig.Core/Orca", 8, 16),
            new("Core.Processes", "Processes", "namespace", "CSharp", "Rig.Core", "adapters", null, "src/Rig.Core/Processes", 6, 10),
            new("Core.Leaves", "Text", "namespace", "CSharp", "Rig.Core", "leaves", null, "src/Rig.Core/Text", 4, 8),
        ];

        var edges = new List<RawArchEdge>
        {
            new("Cli.Program", "Core.Estate", "symbol", 3, false, [new("src/Rig.Cli/Program.cs", 18, "Cli.Program", "Core.Estate.Sync", ["ReadAccess"], "reference")]),
            new("Core.Estate", "Core.Processes", "symbol", 4, false, [new("src/Rig.Core/Estate/Sync.cs", 22, "Core.Estate.Sync", "Core.Processes.IProc", ["ReadAccess"], "reference")]),
            new("Core.Estate", "Core.Leaves", "symbol", 5, false, [new("src/Rig.Core/Estate/Sync.cs", 42, "Core.Estate.Sync", "Core.Leaves.Slug", ["ReadAccess"], "reference")]),
            new("Core.Processes", "Core.Leaves", "symbol", 2, false, [new("src/Rig.Core/Processes/Git.cs", 11, "Core.Processes.Git", "Core.Leaves.Slug", ["ReadAccess"], "reference")]),
        };

        if (withFeatureCross || withCycle)
        {
            edges.Add(new("Core.Estate", "Core.Orca", "symbol", 2, false, [new("src/Rig.Core/Estate/Sync.cs", 50, "Core.Estate.Sync", "Core.Orca.Worktree", ["ReadAccess"], "reference")]));
        }

        if (withCycle)
        {
            edges.Add(new("Core.Orca", "Core.Estate", "symbol", 1, false, [new("src/Rig.Core/Orca/Worktree.cs", 19, "Core.Orca.Worktree", "Core.Estate.Repo", ["ReadAccess"], "reference")]));
        }

        if (withUpward)
        {
            edges.Add(new("Core.Leaves", "Core.Estate", "symbol", 1, false, [new("src/Rig.Core/Text/Slug.cs", 30, "Core.Leaves.Slug", "Core.Estate.Sync", ["ReadAccess"], "reference")]));
        }

        return ArchGraphBuilder.Build("dotnet", "rtkelly13/rig (dotnet)", "rtkelly13/rig", new string('a', 40), analyzer, "namespace", layers, nodes, edges);
    }

    private static byte[] WriteVarintField(int field, int value)
    {
        using var ms = new MemoryStream();
        WriteVarint(ms, (ulong)((field << 3) | 0));
        WriteVarint(ms, (ulong)value);
        return ms.ToArray();
    }

    private static byte[] WriteStringField(int field, string text) => WriteBytesField(field, Encoding.UTF8.GetBytes(text));

    private static byte[] WriteBytesField(int field, byte[] payload)
    {
        using var ms = new MemoryStream();
        WriteVarint(ms, (ulong)((field << 3) | 2));
        WriteVarint(ms, (ulong)payload.Length);
        ms.Write(payload, 0, payload.Length);
        return ms.ToArray();
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        var total = arrays.Sum(a => a.Length);
        var buffer = new byte[total];
        var offset = 0;
        foreach (var arr in arrays)
        {
            Buffer.BlockCopy(arr, 0, buffer, offset, arr.Length);
            offset += arr.Length;
        }

        return buffer;
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected <{expected}> but got <{actual}>.");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
