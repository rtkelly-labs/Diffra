using System.Text.Json;
using Diffra.Protocol;

namespace Diffra.Cli;

/// <summary>CLI commands for exporting and importing portable evidence bundles.</summary>
internal static class BundleCommands
{
    private const string ExportUsage = "Usage: diffra bundle export --manifest <file> -o <bundle.zip> [--store <cas-dir>] [--artifacts-dir <dir>] [--delta <file>] [--assessment <file>] [--baseline-ref <file>]";
    private const string ImportUsage = "Usage: diffra bundle import --bundle <bundle.zip> --store <cas-dir>";

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            Console.Error.WriteLine("Usage: diffra bundle <export|import> [options]");
            Console.Error.WriteLine("  " + ExportUsage);
            Console.Error.WriteLine("  " + ImportUsage);
            return 0;
        }

        var subcommand = args[0];
        var remaining = args[1..];

        if (string.Equals(subcommand, "export", StringComparison.Ordinal))
        {
            return RunExport(remaining);
        }

        if (string.Equals(subcommand, "import", StringComparison.Ordinal))
        {
            return RunImport(remaining);
        }

        Console.Error.WriteLine($"Unknown bundle subcommand '{subcommand}'.");
        return 2;
    }

    private static int RunExport(string[] args)
    {
        string? manifestPath = null;
        string? outputPath = null;
        string? storePath = null;
        string? artifactsDir = null;
        string? deltaPath = null;
        string? assessmentPath = null;
        string? baselineRefPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            var opt = args[i];
            if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
            {
                Console.Error.WriteLine($"Option '{opt}' requires a value.");
                return 2;
            }

            var val = args[++i];
            switch (opt)
            {
                case "--manifest": manifestPath = val; break;
                case "-o": case "--output": outputPath = val; break;
                case "--store": storePath = val; break;
                case "--artifacts-dir": artifactsDir = val; break;
                case "--delta": deltaPath = val; break;
                case "--assessment": assessmentPath = val; break;
                case "--baseline-ref": baselineRefPath = val; break;
                default:
                    Console.Error.WriteLine($"Unknown export option '{opt}'.");
                    Console.Error.WriteLine(ExportUsage);
                    return 2;
            }
        }

        if (manifestPath is null || outputPath is null)
        {
            Console.Error.WriteLine("Both --manifest and -o/--output are required.");
            Console.Error.WriteLine(ExportUsage);
            return 2;
        }

        try
        {
            var options = new EvidenceBundle.ExportOptions(
                manifestPath,
                outputPath,
                storePath,
                artifactsDir,
                deltaPath,
                assessmentPath,
                baselineRefPath);

            EvidenceBundle.Export(options);
            Console.WriteLine($"Successfully exported bundle to {outputPath}");
            return 0;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"Export failed: {ex.Message}");
            return 2;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Export validation error: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Export failed: {ex.Message}");
            return 1;
        }
    }

    private static int RunImport(string[] args)
    {
        string? bundlePath = null;
        string? storePath = null;

        for (var i = 0; i < args.Length; i++)
        {
            var opt = args[i];
            if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
            {
                Console.Error.WriteLine($"Option '{opt}' requires a value.");
                return 2;
            }

            var val = args[++i];
            switch (opt)
            {
                case "--bundle": bundlePath = val; break;
                case "--store": storePath = val; break;
                default:
                    Console.Error.WriteLine($"Unknown import option '{opt}'.");
                    Console.Error.WriteLine(ImportUsage);
                    return 2;
            }
        }

        if (bundlePath is null || storePath is null)
        {
            Console.Error.WriteLine("Both --bundle and --store are required.");
            Console.Error.WriteLine(ImportUsage);
            return 2;
        }

        try
        {
            var cas = new ContentAddressedStore(storePath);
            var result = EvidenceBundle.Import(bundlePath, cas);
            Console.WriteLine($"Successfully imported bundle for manifest {result.ManifestId}: {result.DocumentsCount} documents, {result.ArtifactsCount} artifacts.");
            return 0;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"Import failed: {ex.Message}");
            return 2;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Import validation error: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Import failed: {ex.Message}");
            return 1;
        }
    }
}
