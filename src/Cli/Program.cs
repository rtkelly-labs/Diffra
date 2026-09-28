using System.Text;
using System.Text.Json;
using Diffra.Cli;
using Diffra.Protocol;

return Run(args);

static int Run(string[] arguments)
{
    if (arguments.Length == 0 || arguments[0] is "--help" or "-h" or "help")
    {
        PrintUsage();
        return 0;
    }

    if (string.Equals(arguments[0], "collect", StringComparison.Ordinal))
    {
        return CollectCommand.Run(arguments[1..]);
    }

    if (string.Equals(arguments[0], "diff", StringComparison.Ordinal))
    {
        return DiffEvalCommands.RunDiff(arguments[1..]);
    }

    if (string.Equals(arguments[0], "eval", StringComparison.Ordinal))
    {
        return DiffEvalCommands.RunEval(arguments[1..]);
    }

    if (string.Equals(arguments[0], "bundle", StringComparison.Ordinal))
    {
        return BundleCommands.Run(arguments[1..]);
    }

    if (string.Equals(arguments[0], "present", StringComparison.Ordinal))
    {
        return PresentCommand.Run(arguments[1..]);
    }

    if (!string.Equals(arguments[0], "report", StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"Unknown command '{arguments[0]}'.");
        PrintUsage();
        return 2;
    }

    try
    {
        var options = ParseReportArguments(arguments.AsSpan(1));
        var deltaBytes = ReadBounded(options.DeltaPath);
        var assessmentBytes = options.AssessmentPath is null ? null : ReadBounded(options.AssessmentPath);
        var baselineBytes = options.BaselineReferencePath is null ? null : ReadBounded(options.BaselineReferencePath);
        var format = options.Format switch
        {
            "html" => ReportFormat.Html,
            "json" => ReportFormat.Json,
            "markdown" => ReportFormat.Markdown,
            _ => throw new ArgumentException($"Unsupported report format '{options.Format}'. Choose html, json, or markdown."),
        };

        var report = ReportRenderer.Render(deltaBytes, assessmentBytes, baselineBytes, format);
        if (options.OutputPath is null)
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.Write(report);
            return 0;
        }

        EnsureOutputIsDistinct(options.OutputPath, options.DeltaPath, options.AssessmentPath, options.BaselineReferencePath);
        WriteAtomically(options.OutputPath, report);
        return 0;
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        PrintUsage();
        return 2;
    }
    catch (JsonException exception)
    {
        Console.Error.WriteLine($"Invalid report input: {exception.Message}");
        return 2;
    }
    catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
    {
        Console.Error.WriteLine($"Invalid report input: {exception.Message}");
        return 2;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
    {
        Console.Error.WriteLine($"Report failed: {exception.Message}");
        return 1;
    }
}

static ReportArguments ParseReportArguments(ReadOnlySpan<string> arguments)
{
    if (arguments.Length == 0 || arguments[0].StartsWith('-'))
    {
        throw new ArgumentException("The report command requires a Delta JSON file.");
    }

    var deltaPath = arguments[0];
    string? assessmentPath = null;
    string? baselineReferencePath = null;
    string? outputPath = null;
    string format = "html";
    var seen = new HashSet<string>(StringComparer.Ordinal);

    for (var index = 1; index < arguments.Length; index++)
    {
        var option = arguments[index];
        if (option is not ("--assessment" or "--baseline-ref" or "--format" or "-o"))
        {
            throw new ArgumentException($"Unknown report option '{option}'.");
        }

        if (!seen.Add(option))
        {
            throw new ArgumentException($"Report option '{option}' was supplied more than once.");
        }

        if (index + 1 >= arguments.Length || arguments[index + 1].StartsWith('-'))
        {
            throw new ArgumentException($"Report option '{option}' requires a value.");
        }

        var value = arguments[++index];
        switch (option)
        {
            case "--assessment": assessmentPath = value; break;
            case "--baseline-ref": baselineReferencePath = value; break;
            case "--format": format = value; break;
            case "-o": outputPath = value; break;
        }
    }

    return new ReportArguments(deltaPath, assessmentPath, baselineReferencePath, format, outputPath);
}

static byte[] ReadBounded(string path)
{
    var info = new FileInfo(path);
    if (!info.Exists)
    {
        throw new FileNotFoundException($"Input file does not exist: {path}", path);
    }

    if (info.Length > CanonicalJson.MaximumDocumentBytes)
    {
        throw new JsonException($"Input '{path}' exceeds the {CanonicalJson.MaximumDocumentBytes}-byte limit.");
    }

    var bytes = File.ReadAllBytes(path);
    if (bytes.Length > CanonicalJson.MaximumDocumentBytes)
    {
        throw new JsonException($"Input '{path}' exceeds the {CanonicalJson.MaximumDocumentBytes}-byte limit.");
    }

    return bytes;
}

static void EnsureOutputIsDistinct(string outputPath, string deltaPath, string? assessmentPath, string? baselineReferencePath)
{
    var fullOutput = Path.GetFullPath(outputPath);
    if (PathEquals(fullOutput, Path.GetFullPath(deltaPath)) ||
        assessmentPath is not null && PathEquals(fullOutput, Path.GetFullPath(assessmentPath)) ||
        baselineReferencePath is not null && PathEquals(fullOutput, Path.GetFullPath(baselineReferencePath)))
    {
        throw new ArgumentException("The output path must not overwrite a Delta or Assessment input.");
    }
}

static bool PathEquals(string left, string right) =>
    string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

static void WriteAtomically(string outputPath, string contents)
{
    var fullOutput = Path.GetFullPath(outputPath);
    var directory = Path.GetDirectoryName(fullOutput)!;
    var temporary = Path.Combine(directory, $".{Path.GetFileName(fullOutput)}.{Guid.NewGuid():N}.tmp");
    try
    {
        File.WriteAllText(temporary, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, fullOutput, overwrite: true);
    }
    finally
    {
        if (File.Exists(temporary))
        {
            File.Delete(temporary);
        }
    }
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage: diffra <collect|diff|eval|report|bundle|present> [options]");
    Console.Error.WriteLine("  collect --payload <json-file> --subject-id <id> --commit <full-commit> --type-id <id> --type-version <version> --producer-id <id> --producer-version <version> -o <evidence-file>");
    Console.Error.WriteLine("  diff --baseline-ref <reference.json> --baseline-evidence <evidence.json> --evidence <candidate.json> -o <delta.json>");
    Console.Error.WriteLine("  eval --rules <rules.json> --delta <delta.json> -o <assessment.json>");
    Console.Error.WriteLine("  report <delta-file> [--assessment <file>] [--baseline-ref <reference.json>] [--format html|json|markdown] [-o <file>]");
    Console.Error.WriteLine("  bundle <export|import> [options]");
    Console.Error.WriteLine("  present <bundle|serve> [options]");
}

internal sealed record ReportArguments(string DeltaPath, string? AssessmentPath, string? BaselineReferencePath, string Format, string? OutputPath);
