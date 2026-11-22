using System.Text;
using System.Text.Json;
using Diffra.Converters;
using Diffra.Protocol;

namespace Diffra.Cli;

/// <summary>
/// <c>diffra arch &lt;convert|view|diff&gt;</c>: converts SCIP / dependency-cruiser / go-list / JGF v2.1
/// outputs into canonical <c>diffra/arch-graph@1</c> JSON, renders current system state, and diffs
/// project shape across commits in HTML (current system state + diff overlay), SVG, Markdown,
/// PR Comment, Agent Markdown, JSON, or verified Delta v1.
/// </summary>
internal static class ArchCommand
{
    internal const string Usage = "Usage: arch <convert|view|diff> [options]";

    private const string ConvertUsage =
        "Usage: arch convert --format <jgf-arch|scip|depcruise|go-list> --input <file> [--target <id>] [--layers <a,b,c>] [--slice-depth <n>] [-o <file>]";

    private const string ViewUsage =
        "Usage: arch view --input <file> [--format html|svg|markdown|json] [--input-format <jgf-arch|scip|depcruise|go-list>] [--layers <a,b,c>] [--slice-depth <n>] [-o <file>]";

    private const string DiffUsage =
        "Usage: arch diff --baseline <file> --candidate <file> [--baseline-format <fmt>] [--candidate-format <fmt>] [--layers <a,b,c>] [--slice-depth <n>] [--format html|svg|markdown|comment|agent|delta] [-o <file>]";

    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            Console.WriteLine(Usage);
            Console.WriteLine("  " + ConvertUsage["Usage: ".Length..]);
            Console.WriteLine("  " + ViewUsage["Usage: ".Length..]);
            Console.WriteLine("  " + DiffUsage["Usage: ".Length..]);
            return 0;
        }

        return args[0] switch
        {
            "convert" => RunConvert(args[1..]),
            "view" => RunView(args[1..]),
            "diff" => RunDiff(args[1..]),
            _ => UnknownSubcommand(args[0]),
        };
    }

    private static int RunConvert(string[] args)
    {
        if (args is ["--help" or "-h"])
        {
            Console.WriteLine(ConvertUsage);
            return 0;
        }

        try
        {
            var opts = ParseOptions(args, ["--format", "--input", "--target", "--layers", "--slice-depth", "-o"]);
            var format = Require(opts, "--format");
            var inputPath = Require(opts, "--input");
            var target = opts.GetValueOrDefault("--target");
            var layers = ParseLayers(opts.GetValueOrDefault("--layers"));
            var sliceDepth = ParseOptionalPositiveInt(opts.GetValueOrDefault("--slice-depth"), "--slice-depth");
            var outputPath = opts.GetValueOrDefault("-o");

            var bytes = ReadBounded(inputPath);
            var graph = ArchGraphConverter.ParseDocument(bytes, format, target, layers, sliceDepth);
            var json = Encoding.UTF8.GetString(ArchGraphJson.SerializeJgf(graph));
            return WriteResult(json, outputPath, [inputPath]);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidDataException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine($"Arch convert failed: {exception.Message}");
            return 1;
        }
    }

    private static int RunView(string[] args)
    {
        if (args is ["--help" or "-h"])
        {
            Console.WriteLine(ViewUsage);
            return 0;
        }

        try
        {
            var opts = ParseOptions(args, ["--input", "--format", "--input-format", "--target", "--layers", "--slice-depth", "-o"]);
            var inputPath = Require(opts, "--input");
            var outputFormat = opts.GetValueOrDefault("--format") ?? "html";
            var inputFormat = opts.GetValueOrDefault("--input-format") ?? InferFormat(inputPath);
            var target = opts.GetValueOrDefault("--target");
            var layers = ParseLayers(opts.GetValueOrDefault("--layers"));
            var sliceDepth = ParseOptionalPositiveInt(opts.GetValueOrDefault("--slice-depth"), "--slice-depth");
            var outputPath = opts.GetValueOrDefault("-o");

            var bytes = ReadBounded(inputPath);
            var graph = ArchGraphConverter.ParseDocument(bytes, inputFormat, target, layers, sliceDepth);
            var rendered = outputFormat switch
            {
                "html" => ArchGraphReportRenderer.RenderCurrentHtml(graph),
                "svg" => ArchGraphReportRenderer.RenderCurrentSvg(graph),
                "json" => Encoding.UTF8.GetString(ArchGraphJson.SerializeJgf(graph)),
                "markdown" => RenderCurrentMarkdown(graph),
                _ => throw new ArgumentException($"Unsupported view format '{outputFormat}'. Choose html, svg, markdown or json."),
            };

            return WriteResult(rendered, outputPath, [inputPath]);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidDataException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine($"Arch view failed: {exception.Message}");
            return 1;
        }
    }

    private static int RunDiff(string[] args)
    {
        if (args is ["--help" or "-h"])
        {
            Console.WriteLine(DiffUsage);
            return 0;
        }

        try
        {
            var opts = ParseOptions(args, [
                "--baseline", "--candidate", "--baseline-format", "--candidate-format",
                "--target", "--layers", "--slice-depth", "--format", "-o",
            ]);
            var baselinePath = Require(opts, "--baseline");
            var candidatePath = Require(opts, "--candidate");
            var baselineFormat = opts.GetValueOrDefault("--baseline-format") ?? InferFormat(baselinePath);
            var candidateFormat = opts.GetValueOrDefault("--candidate-format") ?? InferFormat(candidatePath);
            var target = opts.GetValueOrDefault("--target");
            var layers = ParseLayers(opts.GetValueOrDefault("--layers"));
            var sliceDepth = ParseOptionalPositiveInt(opts.GetValueOrDefault("--slice-depth"), "--slice-depth");
            var outputFormat = opts.GetValueOrDefault("--format") ?? "html";
            var outputPath = opts.GetValueOrDefault("-o");

            var baseBytes = ReadBounded(baselinePath);
            var candBytes = ReadBounded(candidatePath);
            var baseline = ArchGraphConverter.ParseDocument(baseBytes, baselineFormat, target, layers, sliceDepth);
            var candidate = ArchGraphConverter.ParseDocument(candBytes, candidateFormat, target, layers, sliceDepth);
            var diff = ArchGraphComparator.Compare(baseline, candidate);

            var rendered = outputFormat switch
            {
                "html" => ArchGraphReportRenderer.RenderDiffHtml(diff),
                "svg" => ArchGraphReportRenderer.RenderDiffOverlaySvg(diff),
                "markdown" => ArchGraphReportRenderer.RenderDiffMarkdown(diff),
                "comment" => ArchGraphReportRenderer.RenderDiffComment(diff),
                "agent" => ArchGraphReportRenderer.RenderDiffAgent(diff),
                "delta" => Encoding.UTF8.GetString(ArchGraphComparator.ToDeltaV1Bytes(
                    diff,
                    EvidenceIdentity.ComputePayloadDigest(ArchGraphJson.SerializeJgf(baseline)),
                    EvidenceIdentity.ComputePayloadDigest(ArchGraphJson.SerializeJgf(baseline)),
                    EvidenceIdentity.ComputePayloadDigest(ArchGraphJson.SerializeJgf(candidate)))),
                _ => throw new ArgumentException($"Unsupported diff format '{outputFormat}'. Choose html, svg, markdown, comment, agent or delta."),
            };

            return WriteResult(rendered, outputPath, [baselinePath, candidatePath]);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidDataException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine($"Arch diff failed: {exception.Message}");
            return 1;
        }
    }

    private static string RenderCurrentMarkdown(ArchGraphDocument graph)
    {
        var sb = new StringBuilder();
        sb.Append("# Architecture state: ").Append(graph.Label).Append("\n\n");
        sb.Append("- Analyzer: `").Append(graph.Analyzer.Tool).Append("` (`").Append(graph.Analyzer.Resolution).Append("`)\n");
        sb.Append("- Nodes: ").Append(graph.Summary.NodeCount).Append(" | Edges: ").Append(graph.Summary.EdgeCount)
            .Append(" | Cycles: ").Append(graph.Summary.CycleCount)
            .Append(" | Layer violations: ").Append(graph.Summary.LayerViolationCount)
            .Append(" | Feature cross-edges: ").Append(graph.Summary.FeatureCrossEdgeCount).Append('\n');
        return sb.ToString();
    }

    private static string InferFormat(string path) =>
        path.EndsWith(".scip", StringComparison.OrdinalIgnoreCase)
            ? ArchGraphConverter.FormatScip
            : ArchGraphConverter.FormatJgfArch;

    private static string[]? ParseLayers(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? null
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int? ParseOptionalPositiveInt(string? raw, string optionName)
    {
        if (raw is null)
        {
            return null;
        }

        if (!int.TryParse(raw, out var value) || value < 1)
        {
            throw new ArgumentException($"Option '{optionName}' must be an integer >= 1.");
        }

        return value;
    }

    private static Dictionary<string, string> ParseOptions(string[] args, string[] allowed)
    {
        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var opt = args[i];
            if (!allowedSet.Contains(opt))
            {
                throw new ArgumentException($"Unknown option '{opt}'.");
            }

            if (values.ContainsKey(opt))
            {
                throw new ArgumentException($"Option '{opt}' was supplied more than once.");
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
            {
                throw new ArgumentException($"Option '{opt}' requires a value.");
            }

            values[opt] = args[++i];
        }

        return values;
    }

    private static string Require(Dictionary<string, string> opts, string name) =>
        opts.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Required option '{name}' was not supplied.");

    private static byte[] ReadBounded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"Input file does not exist: {path}", path);
        }

        if (info.Length > ArchGraphConverter.MaximumFileBytes)
        {
            throw new JsonException($"Input '{path}' exceeds the {ArchGraphConverter.MaximumFileBytes}-byte safety limit.");
        }

        return File.ReadAllBytes(path);
    }

    private static int WriteResult(string content, string? outputPath, string[] inputPaths)
    {
        if (outputPath is null)
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.Write(content);
            return 0;
        }

        foreach (var input in inputPaths)
        {
            if (PathGuard.SameFile(outputPath, input))
            {
                throw new ArgumentException("The output path must not overwrite an input file.");
            }
        }

        var full = Path.GetFullPath(outputPath);
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tmp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }

        return 0;
    }

    private static int UnknownSubcommand(string sub)
    {
        Console.Error.WriteLine($"Unknown arch subcommand '{sub}'.");
        Console.Error.WriteLine(Usage);
        return 2;
    }
}
