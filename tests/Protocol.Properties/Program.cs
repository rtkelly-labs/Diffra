// Property-based and metamorphic checks (CsCheck) for canonical JSON, keyed structural comparison,
// and the numeric diff then eval pipeline.
//
//   Diffra.Protocol.Properties [iterations] [seed]
//
// Every property runs to completion so that one failure does not hide another. A failing property
// prints CsCheck's shrunk counterexample and the seed that reproduces it.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CsCheck;
using Diffra.Protocol;
using Diffra.Protocol.Properties;

var iterations = args.Length > 0 ? long.Parse(args[0], CultureInfo.InvariantCulture) : 2000;
var seed = args.Length > 1 && args[1].Length > 0 ? args[1] : null;
var results = new List<PropertyResult>();

// ---- Canonical JSON -------------------------------------------------------------------------

Check(
    "canonical: canonicalization is idempotent",
    Generators.Document,
    document =>
    {
        var once = CanonicalJson.Canonicalize(Json.Utf8(document));
        var twice = CanonicalJson.Canonicalize(once);
        Assert.Bytes(once, twice, "C(C(x)) != C(x)");
    },
    document => Json.Render(document));

Check(
    "canonical: identity ignores member order, whitespace, escape forms and -0",
    Gen.Select(Generators.Document, Gen.Int),
    input =>
    {
        var (document, noiseSeed) = input;
        var plain = Json.Utf8(document);
        var noisy = Json.Utf8(document, Noise.All(noiseSeed));
        Assert.Bytes(CanonicalJson.Canonicalize(plain), CanonicalJson.Canonicalize(noisy), "canonical bytes differ");
        Assert.Equal(
            EvidenceIdentity.ComputePayloadDigest(plain),
            EvidenceIdentity.ComputePayloadDigest(noisy),
            "payload digest differs");
    },
    input => Json.Render(input.Item1) + "  noisy: " + Json.Render(input.Item1, Noise.All(input.Item2)));

// ---- Keyed structural comparison ------------------------------------------------------------

Check(
    "keyed: Compare does not throw on any document in the v1 subset",
    Gen.Select(Generators.Document, Generators.Document),
    input =>
    {
        var (baseline, candidate) = input;
        _ = Structural.Compare(Json.Render(baseline), Json.Render(candidate), ArrayComparisonMode.KeyedCollection);
        _ = Structural.Compare(Json.Render(baseline), Json.Render(candidate), ArrayComparisonMode.OrderedSequence);
    },
    input => Json.Render(input.Item1) + "  vs  " + Json.Render(input.Item2));

Check(
    "keyed: member order and whitespace produce no changes",
    Gen.Select(Generators.Document, Gen.Int),
    input =>
    {
        var (document, noiseSeed) = input;
        var changes = Structural.Compare(
            Json.Render(document),
            Json.Render(document, new Noise(noiseSeed, Reorder: true, Whitespace: true, Escapes: false, NegativeZero: false)),
            ArrayComparisonMode.OrderedSequence);
        Assert.Empty(changes);
    },
    input => Json.Render(input.Item1));

Check(
    "keyed: inputs with equal canonical identity produce no changes (escape forms, -0)",
    Gen.Select(Generators.Document, Gen.Int),
    input =>
    {
        var (document, noiseSeed) = input;
        var noisy = Json.Render(document, new Noise(noiseSeed, Reorder: false, Whitespace: false, Escapes: true, NegativeZero: true));
        var plain = Json.Render(document);
        Assert.Bytes(
            CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(plain)),
            CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(noisy)),
            "precondition: canonical identity differs");
        Assert.Empty(Structural.Compare(plain, noisy, ArrayComparisonMode.OrderedSequence));
    },
    input => Json.Render(input.Item1) + "  noisy: " + Json.Render(input.Item1, new Noise(input.Item2, false, false, true, true)));

Check(
    "keyed: change paths are unambiguous",
    Gen.Select(Generators.Document, Generators.Document),
    input =>
    {
        var changes = Structural.Compare(Json.Render(input.Item1), Json.Render(input.Item2), ArrayComparisonMode.OrderedSequence);
        var duplicate = changes.GroupBy(change => change.Path, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            Assert.Fail($"path '{duplicate.Key}' reported {duplicate.Count()} times: {string.Join(", ", duplicate.Select(change => change.Kind))}");
        }
    },
    input => Json.Render(input.Item1) + "  vs  " + Json.Render(input.Item2));

var keyedNonEmpty = 0L;
Check(
    "keyed: permuting keyed collections produces no changes",
    Gen.Select(Generators.KeyedDocument, Gen.Int),
    input =>
    {
        var (document, permutationSeed) = input;
        var permuted = Transform.PermuteKeyed(document, new Random(permutationSeed));
        Assert.Empty(Structural.Compare(Json.Render(document), Json.Render(permuted), ArrayComparisonMode.KeyedCollection));
    },
    input => Json.Render(input.Item1));

Check(
    "keyed: diff is invariant under permuting keyed collections",
    Gen.Select(Generators.KeyedDocument, Generators.KeyedDocument, Gen.Int),
    input =>
    {
        var (baseline, candidate, permutationSeed) = input;
        var random = new Random(permutationSeed);
        var direct = Structural.Normalize(Structural.Compare(Json.Render(baseline), Json.Render(candidate), ArrayComparisonMode.KeyedCollection));
        var permuted = Structural.Normalize(Structural.Compare(
            Json.Render(Transform.PermuteKeyed(baseline, random), new Noise(permutationSeed, true, true, false, false)),
            Json.Render(Transform.PermuteKeyed(candidate, random), new Noise(permutationSeed + 1, true, true, false, false)),
            ArrayComparisonMode.KeyedCollection));
        if (direct.Count > 0)
        {
            Interlocked.Increment(ref keyedNonEmpty);
        }

        Assert.Equal(string.Join("\n", direct), string.Join("\n", permuted), "change sets differ");
    },
    input => Json.Render(input.Item1) + "  vs  " + Json.Render(input.Item2));

// ---- Diff then eval ---------------------------------------------------------------------------

var outcomes = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.Ordinal);
Check(
    "diff-eval: delta and assessment are invariant under member order, whitespace, escapes and -0",
    Gen.Select(Generators.Payload, Gen.Int, Gen.Int),
    input =>
    {
        var (baseline, mutationSeed, noiseSeed) = input;
        var candidate = Transform.Mutate(baseline, new Random(mutationSeed));
        var direct = Pipeline.Run(Json.Render(baseline), Json.Render(candidate));
        var noisy = Pipeline.Run(Json.Render(baseline, Noise.All(noiseSeed)), Json.Render(candidate, Noise.All(noiseSeed + 1)));
        outcomes.AddOrUpdate(direct.Outcome, 1, (_, count) => count + 1);
        Assert.Equal(direct.ToString(), noisy.ToString(), "pipeline result differs");
    },
    input => Json.Render(input.Item1) + "  ->  " + Json.Render(Transform.Mutate(input.Item1, new Random(input.Item2))));

var keyedOutcomes = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.Ordinal);
Check(
    "diff-eval: delta and assessment are invariant under permuting keyed collections",
    Gen.Select(Generators.KeyedPayload, Gen.Int, Gen.Int),
    input =>
    {
        var (baseline, mutationSeed, permutationSeed) = input;
        var candidate = Transform.Mutate(baseline, new Random(mutationSeed));
        var random = new Random(permutationSeed);
        var direct = Pipeline.Run(Json.Render(baseline), Json.Render(candidate));
        var permuted = Pipeline.Run(
            Json.Render(Transform.PermuteKeyed(baseline, random)),
            Json.Render(Transform.PermuteKeyed(candidate, random)));
        keyedOutcomes.AddOrUpdate(direct.Status + "/" + direct.Outcome, 1, (_, count) => count + 1);
        Assert.Equal(direct.ToString(), permuted.ToString(), "pipeline result differs");
    },
    input => Json.Render(input.Item1));

// ---- Report -------------------------------------------------------------------------------------

var report = new StringBuilder();
report.AppendLine("## Property and metamorphic checks");
report.AppendLine();
report.AppendLine(CultureInfo.InvariantCulture, $"Iterations per property: {iterations}; seed: {seed ?? "random"}.");
report.AppendLine();
report.AppendLine("| Property | Result | Seconds |");
report.AppendLine("| --- | --- | --- |");
foreach (var result in results)
{
    report.AppendLine(CultureInfo.InvariantCulture, $"| {result.Name} | {(result.Failure is null ? "pass" : "**FAIL**")} | {result.Elapsed.TotalSeconds:F1} |");
}

report.AppendLine();
report.AppendLine(CultureInfo.InvariantCulture, $"Non-vacuity: keyed diff pairs with at least one change: {keyedNonEmpty}.");
report.AppendLine(CultureInfo.InvariantCulture, $"Diff-eval outcomes (plain payloads): {Describe(outcomes)}.");
report.AppendLine(CultureInfo.InvariantCulture, $"Diff-eval status/outcome (payloads with keyed collections): {Describe(keyedOutcomes)}.");
report.AppendLine();
foreach (var result in results.Where(result => result.Failure is not null))
{
    report.AppendLine(CultureInfo.InvariantCulture, $"### FAIL: {result.Name}");
    report.AppendLine();
    report.AppendLine("```text");
    report.AppendLine(result.Failure);
    report.AppendLine("```");
    report.AppendLine();
}

var text = report.ToString();
Console.WriteLine(text);
var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
if (!string.IsNullOrEmpty(summary))
{
    File.AppendAllText(summary, text);
}

return results.Exists(result => result.Failure is not null) ? 1 : 0;

static string Describe(System.Collections.Concurrent.ConcurrentDictionary<string, long> counts) =>
    string.Join(", ", counts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key} {pair.Value}"));

void Check<T>(string name, Gen<T> gen, Action<T> assert, Func<T, string> print)
{
    var watch = Stopwatch.StartNew();
    try
    {
        gen.Sample(assert, seed: seed, iter: iterations, print: print);
        results.Add(new PropertyResult(name, null, watch.Elapsed));
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        var message = exception.Message.Length > 4000 ? exception.Message[..4000] + "..." : exception.Message;
        results.Add(new PropertyResult(name, message, watch.Elapsed));
        Console.WriteLine($"FAIL {name}");
        Console.WriteLine(message);
    }
}

namespace Diffra.Protocol.Properties
{
    internal sealed record PropertyResult(string Name, string? Failure, TimeSpan Elapsed);

    /// <summary>A JSON value inside Diffra's v1 subset.</summary>
    internal abstract record J;

    internal sealed record JNull : J;

    internal sealed record JBool(bool Value) : J;

    internal sealed record JInt(long Value) : J;

    internal sealed record JStr(string Value) : J;

    internal sealed record JArr(IReadOnlyList<J> Items) : J;

    internal sealed record JObj(IReadOnlyList<(string Name, J Value)> Members) : J;

    /// <summary>Which semantics-preserving rewrites to apply when rendering.</summary>
    internal sealed record Noise(int Seed, bool Reorder, bool Whitespace, bool Escapes, bool NegativeZero)
    {
        public static Noise All(int seed) => new(seed, true, true, true, true);
    }

    internal static class Json
    {
        public static byte[] Utf8(J value, Noise? noise = null) => Encoding.UTF8.GetBytes(Render(value, noise));

        public static string Render(J value, Noise? noise = null)
        {
            var builder = new StringBuilder();
            Write(builder, value, noise, noise is null ? null : new Random(noise.Seed));
            return builder.ToString();
        }

        private static void Write(StringBuilder builder, J value, Noise? noise, Random? random)
        {
            Space(builder, noise, random);
            switch (value)
            {
                case JNull:
                    builder.Append("null");
                    break;
                case JBool boolean:
                    builder.Append(boolean.Value ? "true" : "false");
                    break;
                case JInt integer:
                    builder.Append(integer.Value == 0 && noise is { NegativeZero: true } && random!.Next(2) == 0
                        ? "-0"
                        : integer.Value.ToString(CultureInfo.InvariantCulture));
                    break;
                case JStr text:
                    WriteString(builder, text.Value, noise, random);
                    break;
                case JArr array:
                    builder.Append('[');
                    for (var index = 0; index < array.Items.Count; index++)
                    {
                        if (index > 0)
                        {
                            builder.Append(',');
                        }

                        Write(builder, array.Items[index], noise, random);
                    }

                    Space(builder, noise, random);
                    builder.Append(']');
                    break;
                case JObj obj:
                    builder.Append('{');
                    var members = obj.Members.ToArray();
                    if (noise is { Reorder: true })
                    {
                        random!.Shuffle(members);
                    }

                    for (var index = 0; index < members.Length; index++)
                    {
                        if (index > 0)
                        {
                            builder.Append(',');
                        }

                        Space(builder, noise, random);
                        WriteString(builder, members[index].Name, noise, random);
                        Space(builder, noise, random);
                        builder.Append(':');
                        Write(builder, members[index].Value, noise, random);
                    }

                    Space(builder, noise, random);
                    builder.Append('}');
                    break;
                default:
                    throw new InvalidOperationException("Unknown JSON value.");
            }
        }

        private static void Space(StringBuilder builder, Noise? noise, Random? random)
        {
            if (noise is { Whitespace: true } && random!.Next(4) == 0)
            {
                builder.Append(" \t\n\r"[random.Next(4)]);
            }
        }

        private static void WriteString(StringBuilder builder, string value, Noise? noise, Random? random)
        {
            builder.Append('"');
            var escapePair = false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];

                // Escape both halves of a surrogate pair or neither, so the rendered text stays valid UTF-16.
                var escape = char.IsLowSurrogate(character) && index > 0 && char.IsHighSurrogate(value[index - 1])
                    ? escapePair
                    : noise is { Escapes: true } && random!.Next(4) == 0;
                escapePair = escape;
                if (escape)
                {
                    builder.Append("\\u").Append(((int)character).ToString(random!.Next(2) == 0 ? "x4" : "X4", CultureInfo.InvariantCulture));
                }
                else if (character == '"' || character == '\\')
                {
                    builder.Append('\\').Append(character);
                }
                else if (character == '/' && noise is { Escapes: true })
                {
                    builder.Append(random!.Next(2) == 0 ? "\\/" : "/");
                }
                else if (character < ' ')
                {
                    builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                }
                else
                {
                    builder.Append(character);
                }
            }

            builder.Append('"');
        }
    }

    internal static class Generators
    {
        private const long MaximumSafeInteger = 9_007_199_254_740_991;

        // U+E000 sorts after a surrogate pair in UTF-16 order but before it in code point order.
        private static readonly string PrivateUse = char.ConvertFromUtf32(0xE000);
        private static readonly string Emoji = char.ConvertFromUtf32(0x1F600);

        private static readonly Gen<int> CodePoint = Gen.Frequency<int>(
            (6, Gen.Int[0x20, 0x7E]),
            (1, Gen.Int[0x00, 0x1F]),
            (1, Gen.OneOfConst(0x22, 0x5C, 0x2F, 0x7F, 0x2028, 0x2029, 0xFEFF, 0xFFFF, 0xE000, 0x0301)),
            (2, Gen.Int[0x80, 0xD7FF]),
            (1, Gen.Int[0xE000, 0xFFFF]),
            (2, Gen.Int[0x10000, 0x10FFFF]));

        public static readonly Gen<string> Text = CodePoint.Array[0, 8].Select(points => string.Concat(points.Select(char.ConvertFromUtf32)));

        private static readonly Gen<string> Name = Gen.Frequency<string>(
            (3, Gen.OneOfConst("id", "a", "b", "x", "y", "x/y", "~", "", "value", PrivateUse, Emoji)),
            (1, Text));

        private static readonly Gen<long> Integer = Gen.Frequency<long>(
            (3, Gen.Long[-1000, 1000]),
            (1, Gen.OneOfConst(0L, MaximumSafeInteger, -MaximumSafeInteger, 1L << 52)),
            (1, Gen.Long[-MaximumSafeInteger, MaximumSafeInteger]));

        private static readonly Gen<J> Leaf = Gen.Frequency<J>(
            (1, Gen.Const<J>(new JNull())),
            (1, Gen.Bool.Select(value => (J)new JBool(value))),
            (3, Integer.Select(value => (J)new JInt(value))),
            (3, Text.Select(value => (J)new JStr(value))));

        public static readonly Gen<J> Document = Value(4);

        // Keyed documents draw from small value domains so that two documents overlap.
        private static readonly string[] Ids = ["a", "b", "c", "d", "e"];

        private static readonly Gen<J> SmallLeaf = Gen.Frequency<J>(
            (2, Gen.Int[0, 3].Select(value => (J)new JInt(value))),
            (1, Gen.OneOfConst("x", "y").Select(value => (J)new JStr(value))));

        public static readonly Gen<J> KeyedDocument = KeyedObject(3);

        // Numeric evidence payloads: nested objects whose leaves are integers or {value, unit} measurements.
        private static readonly Gen<J> Measurement = Gen.Frequency<J>(
            (3, Gen.Long[-50, 50].Select(value => (J)new JInt(value))),
            (1, Gen.Select(Gen.Long[0, 50], Gen.OneOfConst("ms", "bytes"), (value, unit) =>
                (J)new JObj([("value", new JInt(value)), ("unit", new JStr(unit))]))));

        private static readonly Gen<string> MetricName = Gen.Frequency<string>(
            (3, Gen.OneOfConst("a", "b", "c", "x/y", "~0", PrivateUse, Emoji)),
            (1, Text.Where(name => name is not "value" and not "unit")));

        public static readonly Gen<J> Payload = Metrics(2);

        private static readonly Gen<J> MetricArray = Gen.Select(Gen.Bool.Array[5], Measurement.Array[5], (present, values) =>
            (J)new JArr(Ids.Select((id, index) => (id, index))
                .Where(item => present[item.index])
                .Select(item => (J)new JObj([("id", new JStr(item.id)), ("count", values[item.index])]))
                .ToArray()));

        public static readonly Gen<J> KeyedPayload = Gen.Select(Metrics(1), MetricArray, (metrics, array) =>
            (J)new JObj([.. ((JObj)metrics).Members.Where(member => member.Name != "items"), ("items", array)]));

        private static Gen<J> Value(int depth) => depth == 0
            ? Leaf
            : Gen.Frequency<J>(
                (3, Leaf),
                (1, Value(depth - 1).Array[0, 4].Select(items => (J)new JArr(items))),
                (2, Object(depth - 1)));

        private static Gen<J> Object(int depth) =>
            Gen.Select(Name, Value(depth)).Array[0, 5].Select(members => (J)new JObj(Distinct(members)));

        private static Gen<J> KeyedValue(int depth) => depth == 0
            ? SmallLeaf
            : Gen.Frequency<J>((2, SmallLeaf), (2, KeyedArray(depth - 1)), (1, KeyedObject(depth - 1)));

        private static Gen<J> KeyedObject(int depth) =>
            Gen.Select(Gen.OneOfConst("p", "q", "r"), KeyedValue(depth)).Array[0, 3].Select(members => (J)new JObj(Distinct(members)));

        private static Gen<J> KeyedArray(int depth) =>
            Gen.Select(
                Gen.Bool.Array[5],
                Gen.Select(Gen.OneOfConst("v", "w", "n"), KeyedValue(depth)).Array[0, 3].Array[5],
                (present, bodies) => (J)new JArr(Ids.Select((id, index) => (id, index))
                    .Where(item => present[item.index])
                    .Select(item => (J)new JObj([("id", new JStr(item.id)), .. Distinct(bodies[item.index])]))
                    .ToArray()));

        private static Gen<J> Metrics(int depth) =>
            Gen.Select(MetricName, depth == 0 ? Measurement : Gen.Frequency<J>((3, Measurement), (1, Metrics(depth - 1))))
                .Array[1, 4]
                .Select(members => (J)new JObj(Distinct(members)));

        private static (string Name, J Value)[] Distinct((string Name, J Value)[] members)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return members.Where(member => seen.Add(member.Name)).ToArray();
        }
    }

    internal static class Transform
    {
        /// <summary>Shuffles every array whose items are all objects with a string "id".</summary>
        public static J PermuteKeyed(J value, Random random)
        {
            switch (value)
            {
                case JArr array:
                    var items = array.Items.Select(item => PermuteKeyed(item, random)).ToArray();
                    if (items.All(item => item is JObj obj && obj.Members.Any(member => member.Name == "id" && member.Value is JStr)))
                    {
                        random.Shuffle(items);
                    }

                    return new JArr(items);
                case JObj obj:
                    return new JObj(obj.Members.Select(member => (member.Name, PermuteKeyed(member.Value, random))).ToArray());
                default:
                    return value;
            }
        }

        /// <summary>Changes some integers, and occasionally drops or adds a member, deterministically per seed.</summary>
        public static J Mutate(J value, Random random)
        {
            switch (value)
            {
                case JInt integer:
                    return random.Next(10) < 4 ? new JInt(integer.Value + random.Next(-20, 21)) : integer;
                case JArr array:
                    return new JArr(array.Items.Where(_ => random.Next(10) != 0).Select(item => Mutate(item, random)).ToArray());
                case JObj obj when obj.Members.Any(member => member.Name == "value"):
                    return new JObj(obj.Members.Select(member => member.Name == "value" ? (member.Name, Mutate(member.Value, random)) : member).ToArray());
                case JObj obj:
                    var members = obj.Members
                        .Where(member => member.Name == "id" || random.Next(12) != 0)
                        .Select(member => member.Name == "id" ? member : (member.Name, Mutate(member.Value, random)))
                        .ToList();
                    if (random.Next(12) == 0 && members.TrueForAll(member => member.Name != "added"))
                    {
                        members.Add(("added", new JInt(random.Next(0, 10))));
                    }

                    return new JObj(members);
                default:
                    return value;
            }
        }
    }

    internal static class Structural
    {
        public static IReadOnlyList<StructuralChange> Compare(string baseline, string candidate, ArrayComparisonMode mode)
        {
            using var before = JsonDocument.Parse(baseline);
            using var after = JsonDocument.Parse(candidate);
            return KeyedCollectionComparator.Compare(before.RootElement, after.RootElement, new KeyedCollectionOptions(mode));
        }

        /// <summary>Change list with Before/After reduced to canonical JSON, so equal values compare equal.</summary>
        public static List<string> Normalize(IReadOnlyList<StructuralChange> changes) =>
            changes.Select(change => $"{change.Path} {change.Kind} {Canonical(change.Before)} {Canonical(change.After)}").ToList();

        private static string Canonical(System.Text.Json.Nodes.JsonNode? node) => node is null
            ? "null"
            : Encoding.UTF8.GetString(CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(node.ToJsonString())));
    }

    internal sealed record PipelineResult(string DeltaId, string Status, string AssessmentId, string Outcome, string Changes);

    internal static class Pipeline
    {
        private const string BaselineDocumentId = "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        private const string BaselineEvidenceId = "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
        private const string CandidateEvidenceId = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string RulesDigest = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        private static readonly NumericPolicy Policy = new("diffra.properties", "1.0.0", RulesDigest, 5m, 20m, 0.1m, 0.5m);

        public static PipelineResult Run(string baselinePayload, string candidatePayload)
        {
            var baseline = Evidence(baselinePayload, BaselineEvidenceId);
            var candidate = Evidence(candidatePayload, CandidateEvidenceId);
            var delta = StructuredNumericComparator.Compare(
                baseline,
                candidate,
                new BaselineEvidenceReference(BaselineDocumentId, BaselineEvidenceId, "trusted"));
            var assessment = AssessmentEvaluator.Evaluate(delta, Policy);
            var changes = string.Join(";", delta.Changes.Select(change => $"{change.Path}:{change.Kind}:{change.Value.Before}->{change.Value.After}"));
            return new PipelineResult(delta.Id, delta.Status, assessment.Id, assessment.Outcome, changes);
        }

        private static NumericEvidence Evidence(string payload, string evidenceId)
        {
            using var document = JsonDocument.Parse(payload);
            return new NumericEvidence(evidenceId, "org.example.metrics", "1.0.0", "all", "properties", document.RootElement.Clone());
        }
    }

    internal static class Assert
    {
        public static void Bytes(byte[] expected, byte[] actual, string message)
        {
            if (!expected.AsSpan().SequenceEqual(actual))
            {
                Fail($"{message}: {Encoding.UTF8.GetString(expected)} vs {Encoding.UTF8.GetString(actual)}");
            }
        }

        public static void Equal(string expected, string actual, string message)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                Fail($"{message}:\n  expected: {expected}\n  actual:   {actual}");
            }
        }

        public static void Empty(IReadOnlyList<StructuralChange> changes)
        {
            if (changes.Count > 0)
            {
                Fail("expected no changes, got: " + string.Join("; ", Structural.Normalize(changes)));
            }
        }

        public static void Fail(string message) => throw new InvalidOperationException(message);
    }
}
