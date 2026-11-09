using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Diffra.Protocol;

/// <summary>Creates a deterministic v1 Evidence document from a JSON payload.</summary>
internal static class CollectCommand
{
    private const string EvidenceSchema = "https://diffra.dev/schemas/evidence-v1.schema.json";
    private const string EmptyConfigDigest = "sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a";
    private const string Usage = "Usage: collect --payload <json-file> --subject-id <id> --commit <full-commit> "
        + "--type-id <id> --type-version <version> --producer-id <id> --producer-version <version> -o <evidence-file>";
    private static readonly Regex CommitPattern = new("^(?:[a-f0-9]{40}|[a-f0-9]{64})$", RegexOptions.CultureInvariant);
    private static readonly Regex TypeIdPattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*(?:\\.[a-z0-9]+(?:-[a-z0-9]+)*)+$", RegexOptions.CultureInvariant);
    private static readonly Regex TypeVersionPattern = new("^[0-9]+\\.[0-9]+(?:\\.[0-9]+)?$", RegexOptions.CultureInvariant);

    /// <summary>Runs the collect command and returns its process exit code.</summary>
    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length > 0 && string.Equals(args[0], "collect", StringComparison.Ordinal))
        {
            args = args[1..];
        }

        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        try
        {
            var options = ParseArguments(args);
            EnsureOutputIsDistinct(options.PayloadPath, options.OutputPath);
            var payloadBytes = ReadBoundedPayload(options.PayloadPath);
            var canonicalPayload = CanonicalJson.Canonicalize(payloadBytes);
            var payloadDigest = EvidenceIdentity.ComputePayloadDigest(canonicalPayload);
            var sdkVersion = ReadSdkVersion();
            var environment = new CollectionEnvironment(
                RuntimeInformation.OSDescription,
                RuntimeInformation.FrameworkDescription,
                sdkVersion);

            var unsignedDocument = BuildEvidence(options, canonicalPayload, payloadDigest, environment, id: null);
            var id = EvidenceIdentity.ComputeId(unsignedDocument);
            var evidence = BuildEvidence(options, canonicalPayload, payloadDigest, environment, id);
            EvidenceIdentity.Verify(evidence);
            WriteAtomically(options.OutputPath, evidence);
            return 0;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine($"Invalid collect input: {exception.Message}");
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or Win32Exception)
        {
            Console.Error.WriteLine($"Collect failed: {exception.Message}");
            return 1;
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"Collect failed: {exception.Message}");
            return 1;
        }
    }

    private static CollectArguments ParseArguments(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option is not ("--payload" or "--subject-id" or "--commit" or "--type-id" or "--type-version"
                or "--producer-id" or "--producer-version" or "-o"))
            {
                throw new ArgumentException($"Unknown collect option '{option}'.");
            }

            if (values.ContainsKey(option))
            {
                throw new ArgumentException($"Collect option '{option}' was supplied more than once.");
            }

            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Collect option '{option}' requires a value.");
            }

            var value = args[++index];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"Collect option '{option}' requires a non-empty value.");
            }

            values.Add(option, value);
        }

        string Required(string name) => values.TryGetValue(name, out var value)
            ? value
            : throw new ArgumentException($"Missing required collect option '{name}'.");

        var result = new CollectArguments(
            Required("--payload"),
            Required("--subject-id"),
            Required("--commit"),
            Required("--type-id"),
            Required("--type-version"),
            Required("--producer-id"),
            Required("--producer-version"),
            Required("-o"));

        if (!CommitPattern.IsMatch(result.Commit))
        {
            throw new ArgumentException("--commit must be a full lowercase 40- or 64-character Git object ID.");
        }

        if (!TypeIdPattern.IsMatch(result.TypeId))
        {
            throw new ArgumentException("--type-id must be a lowercase reverse-DNS identifier.");
        }

        if (!TypeVersionPattern.IsMatch(result.TypeVersion))
        {
            throw new ArgumentException("--type-version must use major.minor or major.minor.patch numeric form.");
        }

        return result;
    }

    private static byte[] ReadBoundedPayload(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"Payload file does not exist: {path}", path);
        }

        if (file.Length > CanonicalJson.MaximumDocumentBytes)
        {
            throw new JsonException($"Payload file exceeds the {CanonicalJson.MaximumDocumentBytes}-byte limit.");
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > CanonicalJson.MaximumDocumentBytes)
        {
            throw new JsonException($"Payload file exceeds the {CanonicalJson.MaximumDocumentBytes}-byte limit.");
        }

        return bytes;
    }

    private static byte[] BuildEvidence(
        CollectArguments options,
        byte[] canonicalPayload,
        string payloadDigest,
        CollectionEnvironment environment,
        string? id)
    {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", EvidenceSchema);
            if (id is not null)
            {
                writer.WriteString("id", id);
            }

            writer.WriteString("status", "produced");
            writer.WriteString("typeId", options.TypeId);
            writer.WriteString("typeVersion", options.TypeVersion);
            writer.WriteString("typeResolution", "recognized");

            writer.WriteStartObject("subject");
            writer.WriteString("kind", "git-repository");
            writer.WriteString("id", options.SubjectId);
            writer.WriteString("commit", options.Commit);
            writer.WriteEndObject();

            writer.WriteStartObject("producer");
            writer.WriteString("id", options.ProducerId);
            writer.WriteString("version", options.ProducerVersion);
            writer.WriteString("configDigest", EmptyConfigDigest);
            writer.WriteEndObject();

            writer.WriteStartObject("environment");
            writer.WriteString("platform", environment.Platform);
            writer.WriteString("runtime", environment.Runtime);
            writer.WriteStartArray("tools");
            writer.WriteStartObject();
            writer.WriteString("id", "dotnet-sdk");
            writer.WriteString("version", environment.SdkVersion);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteStartObject("provenance");
            writer.WriteString("source", $"git:{options.Commit}");
            writer.WriteStartArray("artifactDigests");
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteString("payloadDigest", payloadDigest);
            writer.WritePropertyName("payload");
            writer.WriteRawValue(canonicalPayload, skipInputValidation: false);
            writer.WriteStartArray("artifactRefs");
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        var result = new byte[output.WrittenCount + 1];
        output.WrittenSpan.CopyTo(result);
        result[^1] = (byte)'\n';
        return result;
    }

    private static string ReadSdkVersion()
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = FindSdkWorkingDirectory(),
        };
        startInfo.ArgumentList.Add("--version");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start 'dotnet --version' to record the SDK.");
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("'dotnet --version' did not finish within 10 seconds.");
        }

        var output = process.StandardOutput.ReadToEnd().Trim();
        if (process.ExitCode != 0 || output.Length == 0)
        {
            throw new InvalidOperationException("Could not determine the active .NET SDK version.");
        }

        return output;
    }

    private static string FindSdkWorkingDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return directory.FullName;
            }
        }

        return Environment.CurrentDirectory;
    }

    private static void EnsureOutputIsDistinct(string payloadPath, string outputPath)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(Path.GetFullPath(payloadPath), Path.GetFullPath(outputPath), comparison))
        {
            throw new ArgumentException("The output path must not overwrite the payload file.");
        }
    }

    private static void WriteAtomically(string outputPath, byte[] contents)
    {
        var fullOutput = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullOutput)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullOutput)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, contents);
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

    private sealed record CollectArguments(
        string PayloadPath,
        string SubjectId,
        string Commit,
        string TypeId,
        string TypeVersion,
        string ProducerId,
        string ProducerVersion,
        string OutputPath);

    private sealed record CollectionEnvironment(string Platform, string Runtime, string SdkVersion);
}
