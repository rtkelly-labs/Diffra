// Differential conformance harness: Diffra canonical JSON against RFC 8785 (JCS).
//
//   prepare --out <dir> --seed <n> --count <n> [--vectors <testdata dir>]
//       Copies the RFC 8785 test vectors (input/ and output/) into <dir>/cases and <dir>/expected,
//       generates <count> random documents inside Diffra's accepted JCS subset (written with
//       randomised whitespace, member order and string escapes), and canonicalizes every case
//       with Diffra into <dir>/diffra.
//   compare --out <dir> --references <name,name>
//       Compares Diffra's output with the RFC 8785 expected output and with each independent
//       reference implementation's output in <dir>/<name>. A reference writes <id>.json on
//       success or <id>.err on rejection. It also checks the harness itself: a faithful
//       re-serializer must reproduce Diffra's bytes, every deliberately broken re-serializer must be
//       caught by at least one case, and a fixed list of invalid inputs must be rejected with
//       JsonException.
//
// The only pre-registered divergence is the documented subset rule: Diffra rejects fractional and
// exponent number tokens and integers outside the JavaScript safe range. Anything else fails.
using System.Globalization;
using System.Text;
using System.Text.Json;
using Diffra.CanonicalJson.Differential;
using Diffra.Protocol;

const string NumberSubsetRule = "JSON numbers must be integer tokens within the JavaScript safe-integer range.";

if (args.Length == 0)
{
    return Usage();
}

var options = ParseOptions(args);
return args[0] switch
{
    "prepare" => Prepare(options),
    "compare" => Compare(options),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: prepare --out <dir> --seed <n> --count <n> [--vectors <dir>]");
    Console.Error.WriteLine("       compare --out <dir> --references <name,name>");
    return 2;
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 1; index + 1 < args.Length; index += 2)
    {
        options[args[index].TrimStart('-')] = args[index + 1];
    }

    return options;
}

static int Prepare(Dictionary<string, string> options)
{
    var root = options["out"];
    var seed = int.Parse(options["seed"], CultureInfo.InvariantCulture);
    var count = int.Parse(options["count"], CultureInfo.InvariantCulture);
    var cases = Directory.CreateDirectory(Path.Combine(root, "cases")).FullName;
    var expected = Directory.CreateDirectory(Path.Combine(root, "expected")).FullName;
    var diffra = Directory.CreateDirectory(Path.Combine(root, "diffra")).FullName;

    var vectorCount = 0;
    if (options.TryGetValue("vectors", out var vectors))
    {
        foreach (var input in Directory.GetFiles(Path.Combine(vectors, "input"), "*.json").Order(StringComparer.Ordinal))
        {
            var id = "vector-" + Path.GetFileNameWithoutExtension(input);
            File.Copy(input, Path.Combine(cases, id + ".json"), overwrite: true);
            File.Copy(Path.Combine(vectors, "output", Path.GetFileName(input)), Path.Combine(expected, id + ".json"), overwrite: true);
            vectorCount++;
        }
    }

    var generator = new CaseGenerator(seed);
    for (var index = 0; index < count; index++)
    {
        File.WriteAllBytes(Path.Combine(cases, $"gen-{index:D5}.json"), generator.Next());
    }

    var accepted = 0;
    var rejected = 0;
    foreach (var file in Directory.GetFiles(cases, "*.json"))
    {
        var id = Path.GetFileNameWithoutExtension(file);
        try
        {
            File.WriteAllBytes(Path.Combine(diffra, id + ".json"), CanonicalJson.Canonicalize(File.ReadAllBytes(file)));
            accepted++;
        }
        catch (JsonException exception)
        {
            File.WriteAllText(Path.Combine(diffra, id + ".err"), exception.Message);
            rejected++;
        }
    }

    Console.WriteLine($"seed {seed}: {vectorCount} RFC 8785 vectors, {count} generated cases; Diffra accepted {accepted}, rejected {rejected}.");
    return 0;
}

static int Compare(Dictionary<string, string> options)
{
    var root = options["out"];
    var references = options["references"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var ids = Directory.GetFiles(Path.Combine(root, "cases"), "*.json")
        .Select(path => Path.GetFileNameWithoutExtension(path))
        .Order(StringComparer.Ordinal)
        .ToArray();

    var failures = new List<string>();
    var notes = new List<string>();
    var vectorsMatched = 0;
    var vectorsOutsideSubset = 0;
    var generated = 0;
    var agreements = references.ToDictionary(name => name, _ => 0, StringComparer.Ordinal);
    var referenceVectorAgreements = references.ToDictionary(name => name, _ => 0, StringComparer.Ordinal);
    var faithfulMatches = 0;
    var mutantKills = Reserializer.Mutants.ToDictionary(mutant => mutant, _ => 0);

    foreach (var id in ids)
    {
        var isVector = id.StartsWith("vector-", StringComparison.Ordinal);
        generated += isVector ? 0 : 1;
        var input = Path.Combine(root, "cases", id + ".json");
        var diffra = Outcome.Read(root, "diffra", id);
        var expected = isVector ? File.ReadAllBytes(Path.Combine(root, "expected", id + ".json")) : null;

        if (expected is not null)
        {
            // Oracle sanity: a reference that disagrees with the RFC vectors is not a trustworthy oracle.
            foreach (var reference in references)
            {
                var result = Outcome.Read(root, reference, id);
                if (result.Bytes is not null && result.Bytes.AsSpan().SequenceEqual(expected))
                {
                    referenceVectorAgreements[reference]++;
                }
                else
                {
                    notes.Add($"{id}: reference '{reference}' disagrees with the RFC 8785 expected output ({result.Describe()}).");
                }
            }
        }

        if (diffra.Bytes is null)
        {
            if (isVector && string.Equals(diffra.Error, NumberSubsetRule, StringComparison.Ordinal))
            {
                vectorsOutsideSubset++;
                notes.Add($"{id}: outside the v1 subset by design ({diffra.Error}).");
            }
            else
            {
                failures.Add($"{id}: Diffra rejected an input inside the v1 subset: {diffra.Describe()}");
            }

            continue;
        }

        if (expected is not null)
        {
            if (diffra.Bytes.AsSpan().SequenceEqual(expected))
            {
                vectorsMatched++;
            }
            else
            {
                failures.Add($"{id}: Diffra output differs from the RFC 8785 expected output. {Difference(diffra.Bytes, expected)}");
            }
        }

        foreach (var reference in references)
        {
            var result = Outcome.Read(root, reference, id);
            if (result.Bytes is null)
            {
                failures.Add($"{id}: Diffra accepted but '{reference}' did not: {result.Describe()} (input {input})");
            }
            else if (!result.Bytes.AsSpan().SequenceEqual(diffra.Bytes))
            {
                failures.Add($"{id}: Diffra and '{reference}' disagree. {Difference(diffra.Bytes, result.Bytes)} (input {input})");
            }
            else
            {
                agreements[reference]++;
            }
        }

        // Harness mutation check: re-serialize the accepted input with a faithful writer and with
        // deliberately broken writers. The faithful writer must reproduce Diffra's bytes; each broken
        // writer must disagree with at least one oracle (RFC expected output or a reference) on at
        // least one case, otherwise the corpus could not have caught that bug in Diffra.
        var oracles = references
            .Select(reference => Outcome.Read(root, reference, id).Bytes)
            .Append(expected)
            .OfType<byte[]>()
            .ToArray();
        using (var parsed = JsonDocument.Parse(File.ReadAllBytes(input)))
        {
            var faithful = Reserializer.Write(parsed.RootElement, Mutant.None);
            if (faithful.AsSpan().SequenceEqual(diffra.Bytes))
            {
                faithfulMatches++;
            }
            else
            {
                failures.Add($"{id}: the harness's faithful re-serializer differs from Diffra. {Difference(faithful, diffra.Bytes)}");
            }

            foreach (var mutant in Reserializer.Mutants)
            {
                var broken = Reserializer.Write(parsed.RootElement, mutant);
                if (oracles.Any(oracle => !oracle.AsSpan().SequenceEqual(broken)))
                {
                    mutantKills[mutant]++;
                }
            }
        }
    }

    // Negative cases: inputs outside JSON or outside the v1 subset that Diffra must reject with
    // JsonException. Acceptance, or any other exception type, is a failure.
    var negativeRejected = 0;
    foreach (var (name, bytes) in NegativeCases.All)
    {
#pragma warning disable CA1031 // Any exception type other than JsonException is itself the finding.
        try
        {
            var output = CanonicalJson.Canonicalize(bytes);
            failures.Add($"negative case '{name}': Diffra accepted it and produced {Excerpt(output, 0)}");
        }
        catch (JsonException)
        {
            negativeRejected++;
        }
        catch (Exception exception)
        {
            failures.Add($"negative case '{name}': Diffra threw {exception.GetType().Name} instead of JsonException: {exception.Message}");
        }
#pragma warning restore CA1031
    }

    foreach (var mutant in Reserializer.Mutants.Where(mutant => mutantKills[mutant] == 0))
    {
        failures.Add($"harness mutant '{mutant}' survived: no case distinguishes it from the oracles, so the corpus would miss that bug.");
    }

    var report = new StringBuilder();
    report.AppendLine("## Canonical JSON differential (RFC 8785)");
    report.AppendLine();
    report.AppendLine("| Measure | Value |");
    report.AppendLine("| --- | --- |");
    report.AppendLine(CultureInfo.InvariantCulture, $"| RFC 8785 vectors matched byte for byte | {vectorsMatched} |");
    report.AppendLine(CultureInfo.InvariantCulture, $"| RFC 8785 vectors outside the v1 subset (rejected by design) | {vectorsOutsideSubset} |");
    report.AppendLine(CultureInfo.InvariantCulture, $"| Generated cases | {generated} |");
    foreach (var reference in references)
    {
        report.AppendLine(CultureInfo.InvariantCulture, $"| Agreement with `{reference}` (all cases) | {agreements[reference]} |");
        report.AppendLine(CultureInfo.InvariantCulture, $"| `{reference}` agreement with RFC vectors (oracle check) | {referenceVectorAgreements[reference]} |");
    }

    report.AppendLine(CultureInfo.InvariantCulture, $"| Faithful re-serializer matches Diffra | {faithfulMatches} |");
    report.AppendLine(CultureInfo.InvariantCulture, $"| Harness mutants killed | {Reserializer.Mutants.Count(mutant => mutantKills[mutant] > 0)} of {Reserializer.Mutants.Length} |");
    report.AppendLine(CultureInfo.InvariantCulture, $"| Negative cases rejected with JsonException | {negativeRejected} of {NegativeCases.All.Length} |");
    report.AppendLine(CultureInfo.InvariantCulture, $"| Unexpected divergences | {failures.Count} |");
    report.AppendLine();
    report.AppendLine("| Harness mutant | Cases that kill it |");
    report.AppendLine("| --- | --- |");
    foreach (var mutant in Reserializer.Mutants)
    {
        report.AppendLine(CultureInfo.InvariantCulture, $"| `{mutant}` | {mutantKills[mutant]} |");
    }

    report.AppendLine();
    AppendList(report, "Unexpected divergences", failures);
    AppendList(report, "Notes", notes);

    var text = report.ToString();
    Console.WriteLine(text);
    var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
    if (!string.IsNullOrEmpty(summary))
    {
        File.AppendAllText(summary, text);
    }

    return failures.Count == 0 ? 0 : 1;
}

static void AppendList(StringBuilder report, string title, List<string> items)
{
    if (items.Count == 0)
    {
        return;
    }

    report.AppendLine(CultureInfo.InvariantCulture, $"### {title} ({items.Count})");
    report.AppendLine();
    foreach (var item in items.Take(50))
    {
        report.AppendLine(CultureInfo.InvariantCulture, $"- {item}");
    }

    if (items.Count > 50)
    {
        report.AppendLine(CultureInfo.InvariantCulture, $"- ... and {items.Count - 50} more");
    }

    report.AppendLine();
}

static string Difference(byte[] actual, byte[] other)
{
    var length = Math.Min(actual.Length, other.Length);
    var offset = 0;
    while (offset < length && actual[offset] == other[offset])
    {
        offset++;
    }

    var start = Math.Max(0, offset - 24);
    return $"First difference at byte {offset}: Diffra {Excerpt(actual, start)} vs {Excerpt(other, start)}";
}

static string Excerpt(byte[] bytes, int start)
{
    if (start >= bytes.Length)
    {
        return "<end>";
    }

    var length = Math.Min(64, bytes.Length - start);
    return JsonSerializer.Serialize(Encoding.UTF8.GetString(bytes, start, length));
}

namespace Diffra.CanonicalJson.Differential
{
    internal sealed record Outcome(byte[]? Bytes, string? Error)
    {
        public static Outcome Read(string root, string implementation, string id)
        {
            var output = Path.Combine(root, implementation, id + ".json");
            if (File.Exists(output))
            {
                return new Outcome(File.ReadAllBytes(output), null);
            }

            var error = Path.Combine(root, implementation, id + ".err");
            return File.Exists(error)
                ? new Outcome(null, File.ReadAllText(error).Trim())
                : new Outcome(null, "<no output>");
        }

        public string Describe() => Bytes is null ? $"rejected: {Error}" : $"{Bytes.Length} bytes";
    }

    /// <summary>
    /// Generates JSON documents inside Diffra's v1 subset, written non-canonically so that both
    /// parsing and serialization are exercised.
    /// </summary>
    internal sealed class CaseGenerator(int seed)
    {
        private const long MaximumSafeInteger = 9_007_199_254_740_991;
        private const int DeepestChain = 60;

        // Pieces chosen so sibling keys share prefixes and differ where UTF-16 code unit order and
        // code point order disagree (U+E000..U+FFFF sorts after surrogate pairs in UTF-16 order).
        private static readonly string[] KeyPieces =
        [
            "", "a", "A", "b", "1", "10", "_", "\u00E9", "e\u0301", "\u00E9", "\u20AC", "\u2028",
            "\uE000", "\uFFFF", "\uFFFD", "\uD83D\uDE00", "\uD800\uDC00", "\uDBFF\uDFFF", "\u0000", "\u001F", "\u007F",
            "\"", "\\", "/", " ",
        ];

        private static readonly long[] InterestingIntegers =
        [
            0, 1, -1, 9, 10, 99, 100, 1_000_000, 4_294_967_295, 4_294_967_296, 2_147_483_647, -2_147_483_648,
            1_000_000_000_000_000, 999_999_999_999_999, MaximumSafeInteger, -MaximumSafeInteger,
            MaximumSafeInteger - 1, -(MaximumSafeInteger - 1), 4_503_599_627_370_496, 4_503_599_627_370_497,
        ];

        private static readonly char[] SpecialCharacters =
        [
            '\u2028', '\u2029', '\uFEFF', '\uFFFD', '\uFFFF', '\u200B', '\u0301', '\u00E9', '\u007F', '\u0080', '\u00A0',
        ];

        private readonly Random random = new(seed);
        private readonly StringBuilder text = new();

        public byte[] Next()
        {
            text.Clear();
            if (random.Next(20) == 0)
            {
                WriteDeepChain(random.Next(DeepestChain / 2, DeepestChain));
            }
            else
            {
                WriteValue(depth: 0, maximumDepth: random.Next(1, 7));
            }

            Whitespace();
            return Encoding.UTF8.GetBytes(text.ToString());
        }

        private void WriteDeepChain(int depth)
        {
            for (var level = 0; level < depth; level++)
            {
                Whitespace();
                if (level % 2 == 0)
                {
                    text.Append('[');
                }
                else
                {
                    text.Append('{');
                    WriteString(RandomKey());
                    text.Append(':');
                }
            }

            WriteLeaf();
            for (var level = depth - 1; level >= 0; level--)
            {
                Whitespace();
                text.Append(level % 2 == 0 ? ']' : '}');
            }
        }

        private void WriteValue(int depth, int maximumDepth)
        {
            Whitespace();
            var roll = random.Next(100);
            var containerChance = depth == 0 ? 90 : depth >= maximumDepth ? 0 : 50;
            if (roll < containerChance * 6 / 10)
            {
                WriteObject(depth, maximumDepth);
            }
            else if (roll < containerChance)
            {
                WriteArray(depth, maximumDepth);
            }
            else
            {
                WriteLeaf();
            }
        }

        private void WriteObject(int depth, int maximumDepth)
        {
            text.Append('{');
            var names = new HashSet<string>(StringComparer.Ordinal);
            var count = random.Next(0, 9);
            for (var index = 0; index < count; index++)
            {
                var name = RandomKey();
                if (!names.Add(name))
                {
                    continue;
                }

                if (names.Count > 1)
                {
                    Whitespace();
                    text.Append(',');
                }

                Whitespace();
                WriteString(name);
                Whitespace();
                text.Append(':');
                WriteValue(depth + 1, maximumDepth);
            }

            Whitespace();
            text.Append('}');
        }

        private void WriteArray(int depth, int maximumDepth)
        {
            text.Append('[');
            var count = random.Next(0, 9);
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    Whitespace();
                    text.Append(',');
                }

                WriteValue(depth + 1, maximumDepth);
            }

            Whitespace();
            text.Append(']');
        }

        private void WriteLeaf()
        {
            Whitespace();
            switch (random.Next(8))
            {
                case 0:
                    text.Append("null");
                    break;
                case 1:
                    text.Append(random.Next(2) == 0 ? "true" : "false");
                    break;
                case 2:
                case 3:
                    WriteInteger();
                    break;
                default:
                    WriteString(RandomString());
                    break;
            }
        }

        private void WriteInteger()
        {
            long value = random.Next(3) switch
            {
                0 => InterestingIntegers[random.Next(InterestingIntegers.Length)] * (random.Next(2) == 0 ? 1 : -1),
                1 => random.Next(-1000, 1001),
                _ => random.NextInt64(-MaximumSafeInteger, MaximumSafeInteger + 1),
            };

            // "-0" is a valid integer token that every implementation must canonicalize to "0".
            text.Append(value == 0 && random.Next(3) == 0 ? "-0" : value.ToString(CultureInfo.InvariantCulture));
        }

        private string RandomKey()
        {
            var builder = new StringBuilder();
            var pieces = random.Next(0, 4);
            for (var index = 0; index < pieces; index++)
            {
                builder.Append(KeyPieces[random.Next(KeyPieces.Length)]);
            }

            if (random.Next(4) == 0)
            {
                builder.Append(RandomString());
            }

            return builder.ToString();
        }

        private string RandomString()
        {
            var builder = new StringBuilder();
            var length = random.Next(0, 13);
            for (var index = 0; index < length; index++)
            {
                switch (random.Next(7))
                {
                    case 0:
                        builder.Append((char)random.Next(0x20, 0x7F));
                        break;
                    case 1:
                        builder.Append((char)random.Next(0x00, 0x20));
                        break;
                    case 2:
                        builder.Append("\"\\/"[random.Next(3)]);
                        break;
                    case 3:
                        builder.Append((char)random.Next(0x7F, 0x100));
                        break;
                    case 4:
                        builder.Append(SpecialCharacters[random.Next(SpecialCharacters.Length)]);
                        break;
                    case 5:
                        builder.Append(random.Next(2) == 0
                            ? (char)random.Next(0x100, 0xD800)
                            : (char)random.Next(0xE000, 0x10000));
                        break;
                    default:
                        builder.Append(char.ConvertFromUtf32(random.Next(0x10000, 0x110000)));
                        break;
                }
            }

            return builder.ToString();
        }

        private void WriteString(string value)
        {
            text.Append('"');
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                switch (character)
                {
                    case '"' or '\\':
                        if (random.Next(3) == 0)
                        {
                            UnicodeEscape(character);
                        }
                        else
                        {
                            text.Append('\\').Append(character);
                        }

                        break;
                    case '/':
                        if (random.Next(3) == 0)
                        {
                            text.Append("\\/");
                        }
                        else
                        {
                            text.Append('/');
                        }

                        break;
                    case < ' ':
                        var shortEscape = character switch
                        {
                            '\b' => "\\b",
                            '\f' => "\\f",
                            '\n' => "\\n",
                            '\r' => "\\r",
                            '\t' => "\\t",
                            _ => null,
                        };
                        if (shortEscape is not null && random.Next(2) == 0)
                        {
                            text.Append(shortEscape);
                        }
                        else
                        {
                            UnicodeEscape(character);
                        }

                        break;
                    default:
                        if (char.IsHighSurrogate(character))
                        {
                            var low = value[++index];
                            if (random.Next(6) == 0)
                            {
                                UnicodeEscape(character);
                                UnicodeEscape(low);
                            }
                            else
                            {
                                text.Append(character).Append(low);
                            }
                        }
                        else if (random.Next(6) == 0)
                        {
                            UnicodeEscape(character);
                        }
                        else
                        {
                            text.Append(character);
                        }

                        break;
                }
            }

            text.Append('"');
        }

        private void UnicodeEscape(char character)
        {
            var hex = ((int)character).ToString("X4", CultureInfo.InvariantCulture);
            text.Append("\\u");
            foreach (var digit in hex)
            {
                text.Append(random.Next(2) == 0 ? char.ToLowerInvariant(digit) : digit);
            }
        }

        private void Whitespace()
        {
            if (random.Next(10) < 7)
            {
                return;
            }

            var count = random.Next(1, 4);
            for (var index = 0; index < count; index++)
            {
                text.Append(" \t\n\r"[random.Next(4)]);
            }
        }
    }

    /// <summary>Deliberate canonicalization bugs used to check that the corpus can detect them.</summary>
    internal enum Mutant
    {
        None,
        InputMemberOrder,
        CodePointMemberOrder,
        EscapeNonAscii,
        EscapeSolidus,
        UppercaseHexEscapes,
        NoShortEscapes,
        EscapeDelete,
        EscapeLineSeparators,
        KeepNegativeZero,
        TrailingNewline,
    }

    /// <summary>
    /// An independent JCS writer for the v1 subset. With <see cref="Mutant.None"/> it must match
    /// Diffra byte for byte; every other mutant introduces one plausible canonicalization bug.
    /// </summary>
    internal static class Reserializer
    {
        public static readonly Mutant[] Mutants = Enum.GetValues<Mutant>().Where(mutant => mutant != Mutant.None).ToArray();

        public static byte[] Write(JsonElement root, Mutant mutant)
        {
            var builder = new StringBuilder();
            WriteValue(builder, root, mutant);
            if (mutant == Mutant.TrailingNewline)
            {
                builder.Append('\n');
            }

            return Encoding.UTF8.GetBytes(builder.ToString());
        }

        private static void WriteValue(StringBuilder builder, JsonElement element, Mutant mutant)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                {
                    IEnumerable<JsonProperty> properties = element.EnumerateObject();
                    properties = mutant switch
                    {
                        Mutant.InputMemberOrder => properties,
                        Mutant.CodePointMemberOrder => properties.OrderBy(property => property.Name, CodePointComparer.Instance),
                        _ => properties.OrderBy(property => property.Name, StringComparer.Ordinal),
                    };
                    builder.Append('{');
                    var first = true;
                    foreach (var property in properties)
                    {
                        if (!first)
                        {
                            builder.Append(',');
                        }

                        first = false;
                        WriteString(builder, property.Name, mutant);
                        builder.Append(':');
                        WriteValue(builder, property.Value, mutant);
                    }

                    builder.Append('}');
                    break;
                }
                case JsonValueKind.Array:
                {
                    builder.Append('[');
                    var first = true;
                    foreach (var item in element.EnumerateArray())
                    {
                        if (!first)
                        {
                            builder.Append(',');
                        }

                        first = false;
                        WriteValue(builder, item, mutant);
                    }

                    builder.Append(']');
                    break;
                }
                case JsonValueKind.String:
                    WriteString(builder, element.GetString()!, mutant);
                    break;
                case JsonValueKind.Number:
                    var token = element.GetRawText();
                    builder.Append(mutant == Mutant.KeepNegativeZero
                        ? token
                        : long.Parse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    builder.Append(element.GetRawText());
                    break;
            }
        }

        private static void WriteString(StringBuilder builder, string value, Mutant mutant)
        {
            builder.Append('"');
            foreach (var character in value)
            {
                switch (character)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '/' when mutant == Mutant.EscapeSolidus:
                        builder.Append("\\/");
                        break;
                    case '\b' or '\t' or '\n' or '\f' or '\r' when mutant != Mutant.NoShortEscapes:
                        builder.Append(character switch
                        {
                            '\b' => "\\b",
                            '\t' => "\\t",
                            '\n' => "\\n",
                            '\f' => "\\f",
                            _ => "\\r",
                        });
                        break;
                    default:
                        var escape = character < 0x20
                            || (character == 0x7F && mutant == Mutant.EscapeDelete)
                            || (character > 0x7E && mutant == Mutant.EscapeNonAscii)
                            || (character is '\u2028' or '\u2029' && mutant == Mutant.EscapeLineSeparators);
                        if (escape)
                        {
                            builder.Append("\\u").Append(((int)character).ToString(
                                mutant == Mutant.UppercaseHexEscapes ? "X4" : "x4",
                                CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(character);
                        }

                        break;
                }
            }

            builder.Append('"');
        }
    }

    /// <summary>Orders strings by Unicode code point (UTF-8 byte order) instead of UTF-16 code unit.</summary>
    internal sealed class CodePointComparer : IComparer<string>
    {
        public static readonly CodePointComparer Instance = new();

        public int Compare(string? x, string? y) =>
            Encoding.UTF8.GetBytes(x ?? string.Empty).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(y ?? string.Empty));
    }

    /// <summary>Inputs that Diffra's v1 canonicalizer must reject with JsonException.</summary>
    internal static class NegativeCases
    {
        public static readonly (string Name, byte[] Bytes)[] All =
        [
            ("empty input", []),
            ("duplicate member", "{\"a\":1,\"a\":2}"u8.ToArray()),
            ("duplicate member via escape", "{\"a\":1,\"\\u0061\":2}"u8.ToArray()),
            ("nested duplicate member", "[{\"k\":{\"x\":1,\"x\":1}}]"u8.ToArray()),
            ("fraction", "1.5"u8.ToArray()),
            ("zero fraction", "1.0"u8.ToArray()),
            ("exponent", "1e2"u8.ToArray()),
            ("negative zero fraction", "-0.0"u8.ToArray()),
            ("above the safe-integer range", "9007199254740992"u8.ToArray()),
            ("below the safe-integer range", "-9007199254740992"u8.ToArray()),
            ("beyond Int64", "99999999999999999999"u8.ToArray()),
            ("leading zero", "01"u8.ToArray()),
            ("plus sign", "+1"u8.ToArray()),
            ("NaN", "NaN"u8.ToArray()),
            ("Infinity", "[Infinity]"u8.ToArray()),
            ("lone high surrogate escape", "\"\\ud800\""u8.ToArray()),
            ("lone low surrogate escape", "\"\\udc00\""u8.ToArray()),
            ("reversed surrogate escapes", "\"\\udc00\\ud800\""u8.ToArray()),
            ("lone surrogate in member name", "{\"\\ud83d\":1}"u8.ToArray()),
            ("invalid UTF-8 byte", [0x22, 0xFF, 0x22]),
            ("overlong UTF-8", [0x22, 0xC0, 0xAF, 0x22]),
            ("UTF-8 encoded surrogate", [0x22, 0xED, 0xA0, 0x80, 0x22]),
            ("truncated UTF-8", [0x22, 0xE2, 0x82, 0x22]),
            ("byte-order mark", [0xEF, 0xBB, 0xBF, 0x31]),
            ("raw control character in string", [0x22, 0x01, 0x22]),
            ("invalid escape", "\"\\x41\""u8.ToArray()),
            ("trailing comma", "[1,]"u8.ToArray()),
            ("comment", "[1/*c*/]"u8.ToArray()),
            ("single-quoted string", "'a'"u8.ToArray()),
            ("unquoted member name", "{a:1}"u8.ToArray()),
            ("trailing content", "1 2"u8.ToArray()),
            ("nesting deeper than 64", Encoding.ASCII.GetBytes(new string('[', 65) + new string(']', 65))),
        ];
    }
}
