// Coverage-guided fuzzing harness (SharpFuzz + libFuzzer) for Diffra's document parsers and bundle import.
//
//   Diffra.Protocol.Fuzz seed <corpus-root> <repo-root>   write seed corpora for every target
//   Diffra.Protocol.Fuzz replay <target> <file>...         run inputs outside the fuzzer
//   FUZZ_TARGET=<target> Diffra.Protocol.Fuzz              fuzz loop, driven by libfuzzer-dotnet
//
// The oracle is broader than "did it crash". JsonException and InvalidOperationException are the
// rejections the CLI maps to an input error; any other exception type is recorded as a finding,
// deduplicated by exception type and throwing frame, and fuzzing continues so that one timebox can
// collect several distinct findings. Some targets also check invariants on success.
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Diffra.Protocol;
using Diffra.Protocol.Fuzz;
using SharpFuzz;

var mode = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("FUZZ_TARGET") ?? string.Empty;
switch (mode)
{
    case "seed":
        Seeds.Write(args[1], args[2]);
        return 0;
    case "replay":
        foreach (var file in args.Skip(2))
        {
            Console.WriteLine($"{file}: {Findings.Describe(args[1], File.ReadAllBytes(file))}");
        }

        return 0;
    default:
        if (!Targets.Names.Contains(mode, StringComparer.Ordinal))
        {
            Console.Error.WriteLine($"Unknown target '{mode}'. Targets: {string.Join(", ", Targets.Names)}.");
            return 2;
        }

        Fuzzer.LibFuzzer.Run(span => Findings.Guard(mode, span));
        return 0;
}

namespace Diffra.Protocol.Fuzz
{
    /// <summary>A violated invariant on an input the code under test accepted.</summary>
    internal sealed class InvariantException(string message) : Exception(message);

    internal static class Targets
    {
        public static readonly string[] Names = ["canonical", "evidence", "document", "delivery-index", "payloads", "line-ranges", "bundle", "bundle-zip"];

        private static readonly string[] DocumentKinds = ["baseline-reference", "delta", "assessment", "manifest"];

        public static void Run(string target, ReadOnlySpan<byte> data)
        {
            switch (target)
            {
                case "canonical":
                    Canonical(data);
                    break;
                case "evidence":
                    EvidenceIdentity.Verify(data);
                    break;
                case "document":
                    if (data.Length > 0)
                    {
                        DocumentIdentity.Verify(DocumentKinds[data[0] % DocumentKinds.Length], data[1..]);
                    }

                    break;
                case "delivery-index":
                    _ = DeliveryIndex.Verify(data);
                    break;
                case "payloads":
                    Payloads(data);
                    break;
                case "line-ranges":
                    _ = CoveragePayload.ParseLineRanges(Encoding.UTF8.GetString(data));
                    break;
                case "bundle":
                    Bundle.ImportEntries(data);
                    break;
                case "bundle-zip":
                    Bundle.ImportZip(data.ToArray());
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown target.");
            }
        }

        private static void Canonical(ReadOnlySpan<byte> data)
        {
            var once = CanonicalJson.Canonicalize(data);
            var twice = CanonicalJson.Canonicalize(once);
            if (!once.AsSpan().SequenceEqual(twice))
            {
                throw new InvariantException("canonicalization is not idempotent");
            }

            if (Encoding.UTF8.GetByteCount(Encoding.UTF8.GetString(once)) != once.Length || once.Contains((byte)'\n'))
            {
                throw new InvariantException("canonical output is not compact, valid UTF-8");
            }
        }

        private static void Payloads(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
            {
                return;
            }

            using var document = JsonDocument.Parse(data[1..].ToArray());
            var root = document.RootElement;
            switch (data[0] % 4)
            {
                case 0:
                    foreach (var file in CoveragePayload.FromJsonElement(root).Files)
                    {
                        _ = CoveragePayload.ParseLineRanges(file.UncoveredLineRanges);
                    }
                    break;
                case 1:
                    _ = ComplexityPayload.FromJsonElement(root);
                    break;
                case 2:
                    _ = DiffraCloudEvent.FromJsonElement(root);
                    break;
                default:
                    _ = TimeSeriesProjection.FromJsonElement(root);
                    break;
            }
        }
    }

    internal static class Bundle
    {
        private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "diffra-fuzz-" + Environment.ProcessId);
        private static long counter;

        /// <summary>Structured input: NUL-separated name, content, name, content, ... built into a valid zip.</summary>
        public static void ImportEntries(ReadOnlySpan<byte> data)
        {
            var segments = Split(data);
            byte[] zip;
            try
            {
                using var buffer = new MemoryStream();
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    for (var index = 0; index + 1 < segments.Count; index += 2)
                    {
                        var entry = archive.CreateEntry(Encoding.UTF8.GetString(segments[index]), CompressionLevel.NoCompression);
                        using var stream = entry.Open();
                        stream.Write(segments[index + 1]);
                    }
                }

                zip = buffer.ToArray();
            }
            catch (Exception exception) when (exception is ArgumentException or IOException)
            {
                return; // The zip writer refused the entry; nothing reaches Diffra.
            }

            ImportZip(zip);
        }

        public static void ImportZip(byte[] zip)
        {
            var directory = Path.Combine(Scratch, Interlocked.Increment(ref counter).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
            try
            {
                var bundlePath = Path.Combine(directory, "bundle.zip");
                File.WriteAllBytes(bundlePath, zip);
                var store = new ContentAddressedStore(Path.Combine(directory, "store"));
                EvidenceBundle.ImportResult result;
                try
                {
                    result = EvidenceBundle.Import(bundlePath, store);
                }
                catch (Exception exception) when (Findings.IsExpected(exception))
                {
                    if (Directory.EnumerateFiles(store.ObjectsDirectory).Any())
                    {
                        Findings.Observe("partial-import", "Import rejected the bundle after writing objects to the store.", zip);
                    }

                    throw;
                }

                foreach (var digest in result.ImportedDigests)
                {
                    _ = store.Get(digest); // Throws if an imported object does not hash to its address.
                }

                var escaped = Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetFullPath(path))
                    .FirstOrDefault(path => !path.StartsWith(Path.Combine(directory, "store"), StringComparison.Ordinal) && !path.EndsWith("bundle.zip", StringComparison.Ordinal));
                if (escaped is not null)
                {
                    throw new InvariantException($"import wrote outside the store: {escaped}");
                }
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static List<byte[]> Split(ReadOnlySpan<byte> data)
        {
            var segments = new List<byte[]>();
            while (true)
            {
                var index = data.IndexOf((byte)0);
                if (index < 0)
                {
                    segments.Add(data.ToArray());
                    return segments;
                }

                segments.Add(data[..index].ToArray());
                data = data[(index + 1)..];
            }
        }

        public static byte[] Join(params byte[][] segments)
        {
            using var buffer = new MemoryStream();
            for (var index = 0; index < segments.Length; index++)
            {
                if (index > 0)
                {
                    buffer.WriteByte(0);
                }

                buffer.Write(segments[index]);
            }

            return buffer.ToArray();
        }
    }

    internal static class Findings
    {
        private static readonly string OutputDirectory = Environment.GetEnvironmentVariable("FUZZ_FINDINGS") ?? "findings";
        private static readonly HashSet<string> Seen = new(StringComparer.Ordinal);

        public static bool IsExpected(Exception exception) => exception is JsonException or InvalidOperationException;

        public static void Guard(string target, ReadOnlySpan<byte> data)
        {
            try
            {
                Targets.Run(target, data);
            }
            catch (Exception exception) when (!IsExpected(exception))
            {
                Record(target, exception, data.ToArray());
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                // Expected rejection of malformed input.
            }
        }

        public static string Describe(string target, byte[] data)
        {
            try
            {
                Targets.Run(target, data);
                return "accepted";
            }
            catch (Exception exception)
            {
                return $"{(IsExpected(exception) ? "rejected" : Tier(exception))}: {exception.GetType().FullName}: {exception.Message}\n{exception.StackTrace}";
            }
        }

        public static void Observe(string key, string message, byte[] input) =>
            Write("observation", "observation-" + key, message, string.Empty, input);

        private static void Record(string target, Exception exception, byte[] input)
        {
            var frame = new StackTrace(exception).GetFrames()
                .Select(candidate => candidate.GetMethod())
                .Where(method => method?.DeclaringType?.Namespace?.StartsWith("Diffra.Protocol", StringComparison.Ordinal) == true)
                .Select(method => method!.DeclaringType!.Name + "." + method.Name)
                .FirstOrDefault() ?? "external";
            var key = $"{target}-{exception.GetType().Name}-{frame}";
            Write(Tier(exception), key, $"{exception.GetType().FullName}: {exception.Message}", exception.StackTrace ?? string.Empty, input);
        }

        // contract: malformed input surfaces as a framework exception the CLI reports as an internal error.
        // crash: a defect class that should never escape for any input.
        private static string Tier(Exception exception) => exception switch
        {
            InvariantException => "invariant",
            KeyNotFoundException or ArgumentException or FormatException or InvalidCastException or OverflowException or InvalidDataException => "contract",
            _ => "crash",
        };

        private static void Write(string tier, string key, string message, string stack, byte[] input)
        {
            if (!Seen.Add(key))
            {
                return;
            }

            System.IO.Directory.CreateDirectory(OutputDirectory);
            var name = string.Concat(key.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' ? character : '_'));
            var path = Path.Combine(OutputDirectory, name);
            if (File.Exists(path + ".txt"))
            {
                return;
            }

            File.WriteAllBytes(path + ".bin", input);
            File.WriteAllText(
                path + ".txt",
                $"tier: {tier}\nkey: {key}\n{message}\ninput (base64): {Convert.ToBase64String(input.AsSpan(0, Math.Min(input.Length, 4096)))}\n{stack}\n");
        }
    }

    internal static class Seeds
    {
        private static readonly string[] FixtureFolders = ["examples", "baselines"];

        // One complete document per payload parser, indexed by the selector byte, so mutations get past the required properties.
        private static readonly string[] PayloadSeeds =
        [
            """{"scope":"diffra/protocol","methodology":"coverlet","summary":{"lines":{"total":100,"covered":95,"missed":5,"rate":0.95},"branches":{"total":20,"covered":18,"missed":2,"rate":0.9},"methods":{"total":10,"covered":10,"missed":0,"rate":1}},"files":[{"path":"src/a.cs","lines":{"total":10,"covered":8,"missed":2,"rate":0.8},"branches":{"total":2,"covered":1,"missed":1,"rate":0.5},"uncoveredLineRanges":"45, 88-90"}]}""",
            """{"scope":"diffra/protocol","methodology":"roslyn","unit":"cyclomatic","summary":{"totalMethods":2,"totalComplexity":7,"meanComplexity":3.5,"quantiles":{"p50":3,"p75":4,"p90":4,"p99":4},"outliers":[{"methodId":"A.B()","complexity":4,"linesOfCode":20}]},"methods":[{"methodId":"A.B()","complexity":4,"linesOfCode":20},{"methodId":"A.C()","complexity":3,"linesOfCode":9}]}""",
            """{"specversion":"1.0","id":"sha256:1111111111111111111111111111111111111111111111111111111111111111","source":"diffra://repo/example/subject/core","type":"dev.diffra.evidence.v1","time":"2026-01-02T03:04:05Z","dataschema":"https://diffra.dev/schemas/evidence-v1.schema.json","data":{"value":1},"subject":"commit:2222222222222222222222222222222222222222","diffraoutcome":"pass","diffragithubpr":12,"traceparent":"00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"}""",
            """{"metricId":"diffra.coverage.lines.rate","subjectId":"diffra/core","unit":"percent","points":[{"commit":"1111111111111111111111111111111111111111","timestamp":"2026-01-01T00:00:00Z","value":85,"tag":"v0.1.0"}],"candidate":{"commit":"3333333333333333333333333333333333333333","value":91,"delta":2.5},"thresholds":{"min":80,"max":100}}""",
        ];

        public static void Write(string corpusRoot, string repositoryRoot)
        {
            var fixtures = FixtureFolders
                .Select(folder => Path.Combine(repositoryRoot, folder))
                .Where(System.IO.Directory.Exists)
                .SelectMany(folder => System.IO.Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories))
                .Order(StringComparer.Ordinal)
                .Select(File.ReadAllBytes)
                .ToList();
            fixtures.Add("{}"u8.ToArray());
            fixtures.Add("{\"b\":[1,\"x\",null,true],\"a\":{\"\":-0}}"u8.ToArray());

            var deliveryIndex = Encoding.UTF8.GetBytes(DeliveryIndex.Create(
                "example/project",
                "v1.0.0",
                "0123456789abcdef0123456789abcdef01234567",
                [new DeliveryIndex.AssetEntry("cli", "sha256:" + new string('c', 64), "application/zip", 42, "diffra.zip")]));

            Emit(corpusRoot, "canonical", fixtures);
            Emit(corpusRoot, "evidence", fixtures);
            Emit(corpusRoot, "document", fixtures.SelectMany(fixture => Enumerable.Range(0, 4).Select(kind => Prefix((byte)kind, fixture))));
            Emit(corpusRoot, "delivery-index", [.. fixtures, deliveryIndex]);
            Emit(corpusRoot, "payloads", [
                .. fixtures.SelectMany(fixture => Enumerable.Range(0, 4).Select(kind => Prefix((byte)kind, fixture))),
                .. PayloadSeeds.Select((seed, kind) => Prefix((byte)kind, Encoding.UTF8.GetBytes(seed)))]);
            Emit(corpusRoot, "line-ranges", [
                "14-16, 25"u8.ToArray(), "1"u8.ToArray(), "3-1,7"u8.ToArray(), "0-2147483647"u8.ToArray(),
                // Regression: expanded to ~540 million lines and timed out before ParseLineRanges was bounded.
                Encoding.UTF8.GetBytes("1651-" + new string('0', 91) + "541746516")]);

            var bundles = BundleSeeds(repositoryRoot).ToList();
            Emit(corpusRoot, "bundle", bundles);
            Emit(corpusRoot, "bundle-zip", bundles.Select(ToZip));

            // Inputs that once produced a finding, kept so every campaign replays them first.
            var regressions = Path.Combine(repositoryRoot, "tests", "Protocol.Fuzz", "regressions");
            if (System.IO.Directory.Exists(regressions))
            {
                foreach (var targetDirectory in System.IO.Directory.EnumerateDirectories(regressions))
                {
                    Emit(corpusRoot, Path.GetFileName(targetDirectory), System.IO.Directory.EnumerateFiles(targetDirectory).Order(StringComparer.Ordinal).Select(File.ReadAllBytes));
                }
            }
        }

        private static IEnumerable<byte[]> BundleSeeds(string repositoryRoot)
        {
            var artifact = "{\"tests\":{\"value\":42,\"unit\":\"count\"}}"u8.ToArray();
            var digest = ContentAddressedStore.ComputeDigest(artifact);
            var hex = digest["sha256:".Length..];
            var manifestPath = Path.Combine(repositoryRoot, "examples", "sample-project", "manifest.json");
            var manifest = File.Exists(manifestPath)
                ? Encoding.UTF8.GetBytes(File.ReadAllText(manifestPath).Replace("sha256:" + new string('c', 64), digest, StringComparison.Ordinal))
                : Encoding.UTF8.GetBytes($"{{\"id\":\"sha256:{new string('7', 64)}\",\"artifacts\":[{{\"id\":\"metrics-json\",\"digest\":\"{digest}\",\"path\":\"artifacts/metrics.json\"}}]}}");
            var name = (string value) => Encoding.UTF8.GetBytes(value);

            // A bundle as Export writes it.
            yield return Bundle.Join(
                name("manifest.json"), manifest,
                name("documents/manifest.json"), manifest,
                name("artifacts/artifacts/metrics.json"), artifact,
                name("assets/sha256/" + hex), artifact);

            // Digest mismatch: declared digest does not match the asset content.
            yield return Bundle.Join(name("manifest.json"), manifest, name("assets/sha256/" + new string('0', 64)), artifact);

            // Path escape attempts.
            yield return Bundle.Join(name("manifest.json"), manifest, name("documents/../../escape.json"), artifact);
            yield return Bundle.Join(name("manifest.json"), manifest, name("artifacts/..\\..\\escape.json"), artifact);
            yield return Bundle.Join(name("manifest.json"), manifest, name("/etc/escape"), artifact);

            // Minimal manifests.
            yield return Bundle.Join(name("manifest.json"), "{\"id\":\"x\",\"artifacts\":[]}"u8.ToArray());
            yield return Bundle.Join(name("documents/manifest.json"), "{\"id\":\"x\",\"artifacts\":[{\"path\":\"a\",\"digest\":\"sha256:\"}]}"u8.ToArray());
        }

        private static byte[] ToZip(byte[] structured)
        {
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var parts = Encoding.Latin1.GetString(structured).Split('\0');
                for (var index = 0; index + 1 < parts.Length; index += 2)
                {
                    var entry = archive.CreateEntry(Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(parts[index])));
                    using var stream = entry.Open();
                    stream.Write(Encoding.Latin1.GetBytes(parts[index + 1]));
                }
            }

            return buffer.ToArray();
        }

        private static byte[] Prefix(byte prefix, byte[] data) => [prefix, .. data];

        private static void Emit(string corpusRoot, string target, IEnumerable<byte[]> inputs)
        {
            var directory = Path.Combine(corpusRoot, target);
            System.IO.Directory.CreateDirectory(directory);
            foreach (var input in inputs)
            {
                File.WriteAllBytes(Path.Combine(directory, Convert.ToHexString(SHA256.HashData(input))[..16].ToLowerInvariant()), input);
            }
        }
    }
}
