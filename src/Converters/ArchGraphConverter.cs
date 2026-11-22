using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Diffra.Protocol;

namespace Diffra.Converters;

/// <summary>
/// Built-in converters for <c>diffra/arch-graph@1</c> (Diffra#162):
/// <list type="bullet">
///   <item><c>jgf-arch</c>: strict JGF v2.1 architecture graph (<c>schemas/arch-graph-v1.schema.json</c>).</item>
///   <item><c>scip</c>: binary SCIP Protobuf index (<c>scip.proto</c>), folded into a module/namespace graph.</item>
///   <item><c>depcruise</c>: <c>dependency-cruiser --output-type json</c> (<c>cruise-result.schema.json</c>).</item>
///   <item><c>go-list</c>: <c>go list -json ./...</c> concatenated JSON package stream.</item>
/// </list>
/// </summary>
public static class ArchGraphConverter
{
    public const string DatasetType = ArchGraphBuilder.DatasetType;
    public const string FormatJgfArch = "jgf-arch";
    public const string FormatScip = "scip";
    public const string FormatDepCruise = "depcruise";
    public const string FormatGoList = "go-list";

    public const long MaximumFileBytes = 64L * 1024 * 1024;
    public const long MaximumTotalBytes = 256L * 1024 * 1024;
    public const int MaximumFiles = 1000;

    /// <summary>Supported converter format keys under <c>from:</c> in the root file.</summary>
    public static readonly IReadOnlyList<string> SupportedFormats = [FormatJgfArch, FormatScip, FormatDepCruise, FormatGoList];

    /// <summary>Reads files matched by <paramref name="glob"/> under <paramref name="root"/> and emits a Diffra <c>diffra/arch-graph@1</c> entry.</summary>
    public static ConverterResult Read(string root, string format, string glob, int? maximumKeys, IReadOnlyList<string>? layers = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(glob);

        if (!SupportedFormats.Contains(format, StringComparer.Ordinal))
        {
            return Rejected([], $"Unsupported architecture graph converter '{format}'. Supported: {string.Join(", ", SupportedFormats)}.");
        }

        var segments = FileGlob.Parse(glob, out var globProblem);
        if (segments is null)
        {
            return Rejected([], globProblem);
        }

        var match = FileGlob.Match(root, segments, MaximumFiles);
        var problems = new List<string>();
        foreach (var link in match.Links)
        {
            problems.Add($"'{link}' is a link. Links are not followed, so that no file outside the repository is read; it was not read.");
        }

        foreach (var folder in match.Unreadable)
        {
            problems.Add($"The folder '{folder}' cannot be listed, so the files the glob matches in it are not known.");
        }

        if (match.OverLimit)
        {
            problems.Add($"More than {MaximumFiles.ToString(CultureInfo.InvariantCulture)} files match '{glob}', the most one entry can read.");
        }
        else if (match.Files.Count == 0 && match.Links.Count == 0 && match.Unreadable.Count == 0)
        {
            problems.Add($"No file matches '{glob}'.");
        }

        foreach (var path in match.Files.Where(p => !SizeConverter.IsSafePath(p)))
        {
            problems.Add($"The file name '{path}' cannot be recorded: paths must be valid Unicode with no backslash or control character.");
        }

        if (problems.Count > 0)
        {
            return Rejected([], [.. problems]);
        }

        var ordered = match.Files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var sources = new List<ConverterSource>(ordered.Count);
        var graphs = new List<ArchGraphDocument>(ordered.Count);
        long totalBytes = 0;

        foreach (var relativePath in ordered)
        {
            var fullPath = Path.Combine(root, relativePath);
            byte[] bytes;
            try
            {
                var info = new FileInfo(fullPath);
                if (info.Length > MaximumFileBytes)
                {
                    return Rejected(sources, $"'{relativePath}' is {info.Length} bytes, exceeding the {MaximumFileBytes}-byte safety limit.");
                }

                totalBytes += info.Length;
                if (totalBytes > MaximumTotalBytes)
                {
                    return Rejected(sources, $"Matched files exceed the {MaximumTotalBytes}-byte total safety limit.");
                }

                bytes = File.ReadAllBytes(fullPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return Rejected(sources, $"'{relativePath}' cannot be read: {exception.Message}");
            }

            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            sources.Add(new ConverterSource(relativePath, format, digest));

            try
            {
                graphs.Add(ParseDocument(bytes, format, targetId: null, layers: layers));
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
            {
                return Rejected(sources, $"'{relativePath}' could not be read as '{format}': {exception.Message}");
            }
        }

        var merged = graphs.Count == 1 ? graphs[0] : MergeGraphs(graphs, layers);
        var (dataJson, _, _) = ArchGraphJson.SerializeDatasetData(merged, maximumKeys);
        return new ConverterResult(sources, dataJson, []);
    }

    /// <summary>Parses a single architecture graph file in any supported format into a canonical <see cref="ArchGraphDocument"/>.</summary>
    public static ArchGraphDocument ParseDocument(
        ReadOnlySpan<byte> bytes,
        string format,
        string? targetId = null,
        IReadOnlyList<string>? layers = null,
        int? sliceDepth = null)
    {
        var graph = format switch
        {
            FormatJgfArch => ArchGraphJson.ParseJgf(bytes, targetId, layers),
            FormatScip => ConvertScip(bytes, targetId ?? "scip", layers),
            FormatDepCruise => ConvertDepCruise(bytes, targetId ?? "typescript", layers),
            FormatGoList => ConvertGoList(bytes, targetId ?? "go", layers),
            _ => throw new ArgumentException($"Unsupported architecture graph format '{format}'. Choose one of: {string.Join(", ", SupportedFormats)}."),
        };

        return sliceDepth is { } depth and >= 1 ? ProjectDepth(graph, depth) : graph;
    }

    /// <summary>Folds a raw binary SCIP Protobuf index into a normalized <see cref="ArchGraphDocument"/>.</summary>
    public static ArchGraphDocument ConvertScip(ReadOnlySpan<byte> bytes, string id = "scip", IReadOnlyList<string>? layers = null)
    {
        var index = ScipProtobufReader.Parse(bytes);
        var languageCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var symbolOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        var docModule = new Dictionary<string, string>(StringComparer.Ordinal);
        var nodeFiles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var nodeSymbols = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var nodeLanguage = new Dictionary<string, string>(StringComparer.Ordinal);

        // Pass 1: assign each document to its canonical module/namespace node and record symbol ownership.
        foreach (var doc in index.Documents.OrderBy(d => d.RelativePath, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(doc.RelativePath) || !ArchGraphBuilder.IsSafeRelativePath(doc.RelativePath))
            {
                throw new InvalidDataException($"SCIP document has unsafe or empty relative_path '{doc.RelativePath}'.");
            }

            languageCounts[doc.Language] = languageCounts.GetValueOrDefault(doc.Language) + 1;
            var moduleId = InferScipModuleId(doc);
            docModule[doc.RelativePath] = moduleId;
            nodeLanguage.TryAdd(moduleId, doc.Language);

            if (!nodeFiles.TryGetValue(moduleId, out var files))
            {
                nodeFiles[moduleId] = files = new HashSet<string>(StringComparer.Ordinal);
            }

            files.Add(doc.RelativePath);

            if (!nodeSymbols.TryGetValue(moduleId, out var syms))
            {
                nodeSymbols[moduleId] = syms = new HashSet<string>(StringComparer.Ordinal);
            }

            foreach (var sym in doc.Symbols)
            {
                if (!string.IsNullOrWhiteSpace(sym.Symbol) && !sym.Symbol.StartsWith("local ", StringComparison.Ordinal))
                {
                    symbolOwner[sym.Symbol] = moduleId;
                    syms.Add(sym.Symbol);
                }
            }

            foreach (var occ in doc.Occurrences)
            {
                if ((occ.SymbolRoles & ScipProtobufReader.RoleDefinition) != 0
                    && !string.IsNullOrWhiteSpace(occ.Symbol)
                    && !occ.Symbol.StartsWith("local ", StringComparison.Ordinal))
                {
                    symbolOwner[occ.Symbol] = moduleId;
                    syms.Add(occ.Symbol);
                }
            }
        }

        var primaryLanguage = languageCounts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key)
            .FirstOrDefault() ?? "UnspecifiedLanguage";

        var rawNodes = nodeFiles.Keys
            .Order(StringComparer.Ordinal)
            .Select(modId => new RawArchNode(
                Id: modId,
                Label: ShortLabel(modId),
                Kind: "module",
                Language: nodeLanguage.GetValueOrDefault(modId, primaryLanguage),
                Path: DirectoryOf(nodeFiles[modId].Order(StringComparer.Ordinal).First()),
                FileCount: nodeFiles[modId].Count,
                SymbolCount: nodeSymbols[modId].Count))
            .ToList();

        // Pass 2: fold cross-module occurrences and symbol relationships into evidence-backed edges.
        var rawEdges = new List<RawArchEdge>();
        foreach (var doc in index.Documents.OrderBy(d => d.RelativePath, StringComparer.Ordinal))
        {
            var sourceModule = docModule[doc.RelativePath];
            var primaryDefinedSymbol = doc.Symbols
                .Select(s => s.Symbol)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && !s.StartsWith("local ", StringComparison.Ordinal));

            foreach (var occ in doc.Occurrences)
            {
                if (string.IsNullOrWhiteSpace(occ.Symbol)
                    || occ.Symbol.StartsWith("local ", StringComparison.Ordinal)
                    || (occ.SymbolRoles & ScipProtobufReader.RoleDefinition) != 0)
                {
                    continue;
                }

                if (!symbolOwner.TryGetValue(occ.Symbol, out var targetModule)
                    || string.Equals(sourceModule, targetModule, StringComparison.Ordinal))
                {
                    continue;
                }

                var isImport = (occ.SymbolRoles & ScipProtobufReader.RoleImport) != 0;
                var isTest = (occ.SymbolRoles & ScipProtobufReader.RoleTest) != 0;
                var relation = isTest ? "test-import" : isImport ? "import" : "symbol";
                var roles = ScipProtobufReader.DecodeRoles(occ.SymbolRoles);
                var ev = new ArchEdgeEvidence(
                    doc.RelativePath,
                    occ.OneBasedLine,
                    primaryDefinedSymbol,
                    occ.Symbol,
                    roles,
                    isImport ? "import" : "reference");
                rawEdges.Add(new RawArchEdge(sourceModule, targetModule, relation, 1, false, [ev]));
            }

            foreach (var sym in doc.Symbols)
            {
                foreach (var rel in sym.Relationships)
                {
                    if (string.IsNullOrWhiteSpace(rel.Symbol)
                        || !symbolOwner.TryGetValue(rel.Symbol, out var targetModule)
                        || string.Equals(sourceModule, targetModule, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var relKind = rel.IsImplementation ? "implementation"
                        : rel.IsTypeDefinition ? "type_definition"
                        : rel.IsDefinition ? "definition"
                        : "reference";
                    var ev = new ArchEdgeEvidence(doc.RelativePath, null, sym.Symbol, rel.Symbol, [], relKind);
                    rawEdges.Add(new RawArchEdge(sourceModule, targetModule, "symbol", 1, false, [ev]));
                }
            }
        }

        var analyzer = new ArchAnalyzer("scip-protobuf", index.ToolName, index.ToolVersion, primaryLanguage, "scip-symbol");
        return ArchGraphBuilder.Build(id, id, null, null, analyzer, "module", layers, rawNodes, rawEdges);
    }

    /// <summary>Converts <c>dependency-cruiser --output-type json</c> (<c>cruise-result.schema.json</c>) into <see cref="ArchGraphDocument"/>.</summary>
    public static ArchGraphDocument ConvertDepCruise(ReadOnlySpan<byte> bytes, string id = "typescript", IReadOnlyList<string>? layers = null)
    {
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 256 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("modules", out var modulesEl) || modulesEl.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("dependency-cruiser JSON must be an object with a 'modules' array.");
        }

        var rawNodes = new Dictionary<string, RawArchNode>(StringComparer.Ordinal);
        var rawEdges = new List<RawArchEdge>();

        foreach (var module in modulesEl.EnumerateArray())
        {
            if (module.ValueKind != JsonValueKind.Object || !module.TryGetProperty("source", out var sourceEl) || sourceEl.ValueKind != JsonValueKind.String)
            {
                throw new JsonException("Each dependency-cruiser module must have a string 'source'.");
            }

            var sourcePath = NormalizeRelativePath(sourceEl.GetString()!);
            if (IsExternalNodePath(sourcePath))
            {
                continue;
            }

            var sourceId = StripExtension(sourcePath);
            var lang = sourcePath.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ? "TypeScriptReact"
                : sourcePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ? "TypeScript"
                : "JavaScript";
            rawNodes[sourceId] = new RawArchNode(sourceId, ShortLabel(sourceId), "module", lang, Path: sourcePath, FileCount: 1);

            if (!module.TryGetProperty("dependencies", out var depsEl) || depsEl.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var dep in depsEl.EnumerateArray())
            {
                if (dep.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (dep.TryGetProperty("coreModule", out var coreEl) && coreEl.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                if (dep.TryGetProperty("couldNotResolve", out var unresolvedEl) && unresolvedEl.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                if (!dep.TryGetProperty("resolved", out var resolvedEl) || resolvedEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var targetPath = NormalizeRelativePath(resolvedEl.GetString()!);
                if (IsExternalNodePath(targetPath))
                {
                    continue;
                }

                var targetId = StripExtension(targetPath);
                if (string.Equals(sourceId, targetId, StringComparison.Ordinal))
                {
                    continue;
                }

                var typeOnly = (dep.TryGetProperty("typeOnly", out var toEl) && toEl.ValueKind == JsonValueKind.True)
                    || (dep.TryGetProperty("preCompilationOnly", out var preEl) && preEl.ValueKind == JsonValueKind.True);
                var specifier = dep.TryGetProperty("module", out var modEl) && modEl.ValueKind == JsonValueKind.String
                    ? modEl.GetString()
                    : targetId;
                var relation = typeOnly ? "type-only" : "import";
                var ev = new ArchEdgeEvidence(sourcePath, null, sourceId, specifier, ["Import"], "import");
                rawEdges.Add(new RawArchEdge(sourceId, targetId, relation, 1, typeOnly, [ev]));
            }
        }

        var analyzer = new ArchAnalyzer("depcruise", "dependency-cruiser", null, "TypeScript", "compiler-module");
        return ArchGraphBuilder.Build(id, id, null, null, analyzer, "module", layers, rawNodes.Values, rawEdges);
    }

    /// <summary>Converts a <c>go list -json ./...</c> concatenated JSON stream into <see cref="ArchGraphDocument"/>.</summary>
    public static ArchGraphDocument ConvertGoList(ReadOnlySpan<byte> bytes, string id = "go", IReadOnlyList<string>? layers = null)
    {
        var packages = new List<(string ImportPath, string? ModulePath, IReadOnlyList<string> Imports, IReadOnlyList<string> TestImports, int FileCount)>();
        var text = Encoding.UTF8.GetString(bytes);
        var index = 0;

        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index >= text.Length)
            {
                break;
            }

            var sliceBytes = Encoding.UTF8.GetBytes(text[index..]);
            var reader = new Utf8JsonReader(sliceBytes, new JsonReaderOptions { MaxDepth = 256 });
            using var doc = JsonDocument.ParseValue(ref reader);
            var consumedChars = Encoding.UTF8.GetCharCount(sliceBytes, 0, (int)reader.BytesConsumed);
            index += consumedChars;

            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ImportPath", out var ipEl) || ipEl.ValueKind != JsonValueKind.String)
            {
                throw new JsonException("Each 'go list -json' object must contain a string 'ImportPath'.");
            }

            var importPath = ipEl.GetString()!;
            string? modPath = null;
            if (root.TryGetProperty("Module", out var modEl) && modEl.ValueKind == JsonValueKind.Object
                && modEl.TryGetProperty("Path", out var mpEl) && mpEl.ValueKind == JsonValueKind.String)
            {
                modPath = mpEl.GetString();
            }

            var imports = ReadStringArray(root, "Imports");
            var testImports = ReadStringArray(root, "TestImports").Concat(ReadStringArray(root, "XTestImports")).Distinct(StringComparer.Ordinal).ToArray();
            var goFiles = ReadStringArray(root, "GoFiles").Count;
            packages.Add((importPath, modPath, imports, testImports, goFiles));
        }

        if (packages.Count == 0)
        {
            throw new JsonException("'go list -json' input contained no packages.");
        }

        var internalSet = new HashSet<string>(packages.Select(p => p.ImportPath), StringComparer.Ordinal);
        var rawNodes = packages.Select(p => new RawArchNode(
            Id: p.ImportPath,
            Label: ShortLabel(p.ImportPath),
            Kind: "package",
            Language: "Go",
            Project: p.ModulePath,
            FileCount: p.FileCount)).ToList();

        var rawEdges = new List<RawArchEdge>();
        foreach (var pkg in packages)
        {
            var relPath = pkg.ModulePath is not null && pkg.ImportPath.StartsWith(pkg.ModulePath + "/", StringComparison.Ordinal)
                ? pkg.ImportPath[(pkg.ModulePath.Length + 1)..]
                : "go.mod";
            if (!ArchGraphBuilder.IsSafeRelativePath(relPath))
            {
                relPath = "go.mod";
            }

            foreach (var imp in pkg.Imports.Where(internalSet.Contains))
            {
                var ev = new ArchEdgeEvidence(relPath, null, pkg.ImportPath, imp, ["Import"], "import");
                rawEdges.Add(new RawArchEdge(pkg.ImportPath, imp, "import", 1, false, [ev]));
            }

            foreach (var testImp in pkg.TestImports.Where(i => internalSet.Contains(i) && !pkg.Imports.Contains(i, StringComparer.Ordinal)))
            {
                var ev = new ArchEdgeEvidence(relPath, null, pkg.ImportPath, testImp, ["Import", "Test"], "import");
                rawEdges.Add(new RawArchEdge(pkg.ImportPath, testImp, "test-import", 1, false, [ev]));
            }
        }

        var analyzer = new ArchAnalyzer("go-list", "go list", null, "Go", "compiler-package");
        return ArchGraphBuilder.Build(id, id, null, null, analyzer, "package", layers, rawNodes, rawEdges);
    }

    /// <summary>Projects a graph onto a coarser slice depth (folding child namespaces or directories).</summary>
    public static ArchGraphDocument ProjectDepth(ArchGraphDocument graph, int depth)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (depth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), depth, "Slice depth must be >= 1.");
        }

        var mapping = graph.Nodes.Keys.ToDictionary(id => id, id => FoldId(id, depth), StringComparer.Ordinal);
        var foldedNodes = new Dictionary<string, RawArchNode>(StringComparer.Ordinal);
        foreach (var (id, node) in graph.Nodes)
        {
            var targetId = mapping[id];
            if (!foldedNodes.TryGetValue(targetId, out var existing))
            {
                foldedNodes[targetId] = new RawArchNode(
                    targetId,
                    ShortLabel(targetId),
                    node.Kind,
                    node.Language,
                    node.Project,
                    node.Layer,
                    node.Slice,
                    node.Path,
                    node.FileCount,
                    node.SymbolCount);
            }
            else
            {
                foldedNodes[targetId] = existing with
                {
                    FileCount = existing.FileCount + node.FileCount,
                    SymbolCount = existing.SymbolCount + node.SymbolCount,
                };
            }
        }

        var foldedEdges = graph.Edges
            .Select(e => new RawArchEdge(mapping[e.Source], mapping[e.Target], e.Relation, e.Weight, e.TypeOnly, e.Evidence))
            .Where(e => !string.Equals(e.Source, e.Target, StringComparison.Ordinal))
            .ToList();

        return ArchGraphBuilder.Build(
            graph.Id,
            graph.Label,
            graph.Repo,
            graph.Commit,
            graph.Analyzer,
            graph.Granularity,
            graph.Layers,
            foldedNodes.Values,
            foldedEdges,
            graph.Warnings);
    }

    private static ArchGraphDocument MergeGraphs(List<ArchGraphDocument> graphs, IReadOnlyList<string>? overrideLayers)
    {
        var first = graphs[0];
        var rawNodes = graphs.SelectMany(g => g.Nodes.Values).Select(n => new RawArchNode(
            n.Id, n.Label, n.Kind, n.Language, n.Project, n.Layer, n.Slice, n.Path, n.FileCount, n.SymbolCount));
        var rawEdges = graphs.SelectMany(g => g.Edges).Select(e => new RawArchEdge(
            e.Source, e.Target, e.Relation, e.Weight, e.TypeOnly, e.Evidence));
        var layers = overrideLayers ?? first.Layers;
        var warnings = graphs.SelectMany(g => g.Warnings);
        return ArchGraphBuilder.Build(first.Id, first.Label, first.Repo, first.Commit, first.Analyzer, first.Granularity, layers, rawNodes, rawEdges, warnings);
    }

    private static string InferScipModuleId(ScipDocument doc)
    {
        // Prefer SCIP namespace descriptors (`Foo/Bar/` -> `Foo.Bar`) when present on defined symbols.
        var definedSymbol = doc.Symbols
            .Select(s => s.Symbol)
            .Concat(doc.Occurrences.Where(o => (o.SymbolRoles & ScipProtobufReader.RoleDefinition) != 0).Select(o => o.Symbol))
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && !s.StartsWith("local ", StringComparison.Ordinal));

        if (definedSymbol is not null)
        {
            var ns = ExtractScipNamespace(definedSymbol);
            if (ns is not null)
            {
                return ns;
            }
        }

        return StripExtension(doc.RelativePath);
    }

    private static string? ExtractScipNamespace(string scipSymbol)
    {
        // SCIP grammar: `<scheme> <manager> <pkg> <version> <descriptors>` (4 space-separated header tokens).
        var parts = scipSymbol.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5)
        {
            return null;
        }

        var descriptors = parts[4];
        var lastSlash = descriptors.LastIndexOf('/');
        if (lastSlash <= 0)
        {
            return null;
        }

        var nsPart = descriptors[..lastSlash].Trim('/').Replace('`', ' ').Trim();
        return string.IsNullOrWhiteSpace(nsPart) ? null : nsPart.Replace('/', '.');
    }

    private static string FoldId(string id, int depth)
    {
        if (id.Contains("::", StringComparison.Ordinal))
        {
            var parts = id.Split("::", StringSplitOptions.RemoveEmptyEntries);
            return string.Join("::", parts.Take(Math.Min(depth, parts.Length)));
        }

        if (id.Contains('/', StringComparison.Ordinal))
        {
            var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return string.Join('/', parts.Take(Math.Min(depth, parts.Length)));
        }

        var dotParts = id.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('.', dotParts.Take(Math.Min(depth, dotParts.Length)));
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('.', '/');

    private static bool IsExternalNodePath(string path) =>
        path.StartsWith("node_modules/", StringComparison.Ordinal)
        || path.Contains("/node_modules/", StringComparison.Ordinal)
        || !ArchGraphBuilder.IsSafeRelativePath(path);

    private static string StripExtension(string path)
    {
        var slash = path.LastIndexOf('/');
        var dot = path.LastIndexOf('.');
        return dot > slash ? path[..dot] : path;
    }

    private static string DirectoryOf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash > 0 ? relativePath[..slash] : relativePath;
    }

    private static string ShortLabel(string id)
    {
        var cut = Math.Max(id.LastIndexOf('.'), id.LastIndexOf('/'));
        return cut >= 0 && cut + 1 < id.Length ? id[(cut + 1)..] : id;
    }

    private static List<string> ReadStringArray(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<string>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                list.Add(item.GetString()!);
            }
        }

        return list;
    }

    private static ConverterResult Rejected(IReadOnlyList<ConverterSource> sources, params string[] problems) =>
        new(sources, null, problems);
}
