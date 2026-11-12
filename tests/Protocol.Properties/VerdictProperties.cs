using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsCheck;

namespace Diffra.Protocol.Properties;

/// <summary>
/// The verdicts: <see cref="StructuredNumericComparator.Compare"/> against exact arithmetic,
/// <see cref="AssessmentEvaluator.Evaluate"/> against the rules in <c>docs/cli.md</c>, and
/// <see cref="Enforcement"/> and <see cref="Attention"/> against the statuses they are given. A
/// wrong pass, warn or fail is the tool's core failure.
/// </summary>
internal sealed class VerdictProperties : IPropertySuite
{
    public void Register(Runner runner)
    {
        var deltas = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        var statuses = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        var written = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        var points = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);

        runner.Check(
            "verdicts: diff reports every value that differs, with exact arithmetic and one spelling",
            VerdictInputs.Case,
            input =>
            {
                var expected = VerdictModel.Diff(input.Metrics);
                var delta = VerdictInputs.Compare(input.Baseline, input.Candidate);
                Tally(deltas, expected.Status ?? "near the limit of a decimal");
                VerdictModel.Check(expected, delta);
            },
            VerdictInputs.Print);

        runner.Check(
            "verdicts: eval gives each change the status the documented rules give it",
            Gen.Select(VerdictInputs.Case, VerdictInputs.Recipe),
            input =>
            {
                var delta = VerdictInputs.Compare(input.Item1.Baseline, input.Item1.Candidate);
                var policy = input.Item2.Resolve(delta);
                if (policy.WarnAboveAbsoluteIncrease is null && policy.FailAboveAbsoluteIncrease is null
                    && policy.WarnAboveRelativeIncrease is null && policy.FailAboveRelativeIncrease is null)
                {
                    Tally(statuses, "no threshold (refused)");
                    VerdictModel.Refuses(delta, policy);
                    return;
                }

                var assessment = AssessmentEvaluator.Evaluate(delta, policy);
                VerdictModel.CheckAssessment(delta, policy, assessment);
                foreach (var finding in assessment.Findings)
                {
                    Tally(statuses, finding.Status);
                }

                foreach (var point in delta.Changes.SelectMany(change => VerdictModel.DecisionPoints(change, policy)))
                {
                    Tally(points, point);
                }
            },
            input => VerdictInputs.Print(input.Item1) + "\n  policy recipe: " + input.Item2);

        runner.Check(
            "verdicts: a delta written by hand is judged by the same rules and never passes unread",
            Gen.Select(VerdictInputs.WrittenDelta, VerdictInputs.Recipe),
            input =>
            {
                var policy = input.Item2.Resolve(input.Item1);
                if (policy.WarnAboveAbsoluteIncrease is null && policy.FailAboveAbsoluteIncrease is null
                    && policy.WarnAboveRelativeIncrease is null && policy.FailAboveRelativeIncrease is null)
                {
                    VerdictModel.Refuses(input.Item1, policy);
                    return;
                }

                var assessment = AssessmentEvaluator.Evaluate(input.Item1, policy);
                VerdictModel.CheckAssessment(input.Item1, policy, assessment);
                Tally(written, input.Item1.Status + " -> " + assessment.Outcome);
            },
            input => VerdictInputs.Print(input.Item1) + "\n  policy recipe: " + input.Item2);

        runner.Check(
            "verdicts: comparing in the other direction mirrors every change",
            VerdictInputs.Case,
            input =>
            {
                var forward = VerdictInputs.Compare(input.Baseline, input.Candidate);
                var backward = VerdictInputs.Compare(input.Candidate, input.Baseline);
                VerdictModel.CheckMirror(forward, backward);
            },
            VerdictInputs.Print);

        runner.Check(
            "verdicts: loosening a threshold never makes a finding or the outcome worse",
            Gen.Select(VerdictInputs.Case, VerdictInputs.Recipe, Gen.Int[0, 3], Gen.Int[0, 4]),
            input =>
            {
                var delta = VerdictInputs.Compare(input.Item1.Baseline, input.Item1.Candidate);
                var strict = input.Item2.Resolve(delta);
                var loose = VerdictInputs.Loosen(strict, input.Item3, input.Item4);
                if (loose is null)
                {
                    return;
                }

                var before = AssessmentEvaluator.Evaluate(delta, strict);
                var after = AssessmentEvaluator.Evaluate(delta, loose);
                VerdictModel.CheckNotWorse(before, after);
            },
            input => VerdictInputs.Print(input.Item1) + "\n  policy recipe: " + input.Item2 + $", loosen {input.Item3} by {input.Item4}");

        long failing = 0, warning = 0, quiet = 0;
        runner.Check(
            "verdicts: fail-on is nested, and the headline and the order follow the findings alone",
            Gen.Select(VerdictInputs.Findings, Gen.Int),
            input =>
            {
                var findings = input.Item1.Select(VerdictInputs.Ranked).ToList();
                var model = input.Item1.Select(finding => finding.Status).ToList();
                var fails = model.Contains("fail");
                var warns = model.Exists(status => status is "fail" or "warn" or "needs-review" or "inconclusive");
                Count(ref failing, fails);
                Count(ref warning, warns && !fails);
                Count(ref quiet, model.TrueForAll(status => status == "pass"));
                VerdictModel.Same(false, Enforcement.FailsStep(FailOn.Never, findings), "fail-on never failed the step");
                VerdictModel.Same(fails, Enforcement.FailsStep(FailOn.Fail, findings), "fail-on fail");
                VerdictModel.Same(warns, Enforcement.FailsStep(FailOn.Warn, findings), "fail-on warn");
                var level = fails ? AttentionLevel.Act : model.TrueForAll(status => status == "pass") ? AttentionLevel.Quiet : AttentionLevel.Review;
                VerdictModel.Same(level, Attention.ForFindings(findings), "attention level");

                var ranked = Attention.Rank(findings);
                var shuffled = findings.ToArray();
                new Random(input.Item2).Shuffle(shuffled);
                Assert.Equal(
                    string.Join(" ", ranked.Select(finding => finding.Id)),
                    string.Join(" ", Attention.Rank(shuffled).Select(finding => finding.Id)),
                    "the order depends on the order the findings were given in");
                Assert.Equal(
                    string.Join(" ", input.Item1.OrderBy(VerdictModel.RankKey, StringComparer.Ordinal).Select(finding => finding.Id)),
                    string.Join(" ", ranked.Select(finding => finding.Id)),
                    "the order of the findings");
            },
            input => string.Join("; ", input.Item1.Select(finding => $"{finding.Id} {finding.Status} {finding.Relative ?? "null"}")));

        runner.Note(() => $"Verdict diffs by expected status: {Describe(deltas)}.");
        runner.Note(() => $"Verdict findings by status: {Describe(statuses)}.");
        runner.Note(() => $"Verdict decision points reached: {Describe(points)}.");
        runner.Note(() => $"Hand-written deltas by status and outcome: {Describe(written)}.");
        runner.Note(() => string.Create(
            CultureInfo.InvariantCulture,
            $"Finding lists where fail-on fail trips, only fail-on warn trips, and all pass: {Interlocked.Read(ref failing)}, {Interlocked.Read(ref warning)}, {Interlocked.Read(ref quiet)}."));
    }

    private static void Tally(ConcurrentDictionary<string, long> counts, string key) =>
        counts.AddOrUpdate(key, 1, (_, count) => count + 1);

    private static void Count(ref long counter, bool reached)
    {
        if (reached)
        {
            Interlocked.Increment(ref counter);
        }
    }

    private static string Describe(ConcurrentDictionary<string, long> counts) => counts.IsEmpty
        ? "none"
        : string.Join(", ", counts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key} {pair.Value.ToString(CultureInfo.InvariantCulture)}"));
}

/// <summary>
/// A decimal value held exactly, as a whole number of units of ten to the minus <see cref="Places"/>.
/// The model does all its arithmetic on these, so nothing in it rounds or overflows.
/// </summary>
internal static partial class Exact
{
    public const int Places = 60;

    public static readonly BigInteger One = BigInteger.Pow(10, Places);

    /// <summary>The largest value a decimal holds, as a whole number.</summary>
    public static readonly BigInteger LargestDecimal = BigInteger.Pow(2, 96) - 1;

    /// <summary>
    /// The value of a decimal string: an optional sign, digits and an optional point. Null for
    /// anything else, including exponent notation.
    /// </summary>
    public static BigInteger? Parse(string? text)
    {
        if (text is null || !Plain().IsMatch(text))
        {
            return null;
        }

        var digits = text.TrimStart('+', '-');
        var point = digits.IndexOf('.', StringComparison.Ordinal);
        var whole = point < 0 ? digits : digits[..point];
        var fraction = (point < 0 ? string.Empty : digits[(point + 1)..]).TrimEnd('0');
        if (fraction.Length > Places)
        {
            throw new InvalidOperationException($"The model holds {Places} decimal places, and '{text}' has more.");
        }

        var value = BigInteger.Parse("0" + whole + fraction.PadRight(Places, '0'), NumberStyles.None, CultureInfo.InvariantCulture);
        return text[0] == '-' ? -value : value;
    }

    /// <summary>The one spelling <c>diff</c> writes: no plus sign, exponent, trailing zeros or minus zero.</summary>
    public static string Spell(BigInteger value)
    {
        var digits = BigInteger.Abs(value).ToString(CultureInfo.InvariantCulture).PadLeft(Places + 1, '0');
        var fraction = digits[^Places..].TrimEnd('0');
        return (value.Sign < 0 ? "-" : string.Empty) + digits[..^Places] + (fraction.Length == 0 ? string.Empty : "." + fraction);
    }

    /// <summary>Whether a decimal holds the value without rounding: at most 28 places and 96 bits.</summary>
    public static bool IsHeld(BigInteger value)
    {
        var smallest = BigInteger.Pow(10, Places - 28);
        if (!(value % smallest).IsZero)
        {
            return false;
        }

        var mantissa = BigInteger.Abs(value / smallest);
        for (var scale = 28; scale > 0 && mantissa > LargestDecimal && (mantissa % 10).IsZero; scale--)
        {
            mantissa /= 10;
        }

        return mantissa <= LargestDecimal;
    }

    public static bool IsCanonical(string text) => Canonical().IsMatch(text) && text != "-0";

    [GeneratedRegex(@"\A[+-]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)\z")]
    private static partial Regex Plain();

    [GeneratedRegex(@"\A-?(?:0|[1-9][0-9]*)(?:\.[0-9]*[1-9])?\z")]
    private static partial Regex Canonical();
}

/// <summary>How a measurement's value is written in the payload.</summary>
internal enum ValueForm
{
    /// <summary>A string <c>value</c> in a measurement object.</summary>
    Text,

    /// <summary>A number token <c>value</c> in a measurement object.</summary>
    Number,

    /// <summary>A bare number token, which has no unit.</summary>
    Bare,
}

/// <summary>One side of a measurement: the value as written, its form and its unit.</summary>
internal sealed record Reading(string Value, ValueForm Form, string? Unit);

/// <summary>A measurement in the baseline, the candidate or both.</summary>
internal sealed record Metric(IReadOnlyList<string> Name, Reading? Before, Reading? After)
{
    public string Path => "/" + string.Join("/", Name.Select(segment => segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)));
}

internal sealed record VerdictCase(IReadOnlyList<Metric> Metrics, int Seed)
{
    public string Baseline => VerdictInputs.Render(Metrics, metric => metric.Before, new Random(Seed));

    public string Candidate => VerdictInputs.Render(Metrics, metric => metric.After, new Random(Seed + 1));
}

/// <summary>A finding as the fail-on and attention property draws it.</summary>
internal sealed record DrawnFinding(string Id, string Status, string? Relative);

/// <summary>
/// How to choose the four thresholds once the delta is known: none, a fixed value, or a value at, just
/// above or just below a change's own absolute or relative increase, so that the boundary is reached.
/// </summary>
internal sealed record PolicyRecipe(IReadOnlyList<(int Mode, int Fixed, int Change, int Offset)> Thresholds)
{
    private const string RulesDigest = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static readonly decimal[] FixedValues = [0m, 1m, 5m, 20m, 0.1m, 0.5m, 0.0000000000000000000000000001m, decimal.MaxValue];

    public NumericPolicy Resolve(DeltaDocument delta)
    {
        var modified = delta.Changes.Where(change => change.Kind == "modified").ToList();
        var values = Thresholds.Select((threshold, index) => Threshold(threshold, index >= 2, modified)).ToList();
        return new NumericPolicy("diffra.properties", "1.0.0", RulesDigest, values[0], values[1], values[2], values[3]);
    }

    public override string ToString() => string.Join(" ", Thresholds.Select(threshold => $"({threshold.Mode},{threshold.Fixed},{threshold.Change},{threshold.Offset})"));

    private static decimal? Threshold((int Mode, int Fixed, int Change, int Offset) threshold, bool relative, List<DeltaChange> modified)
    {
        if (threshold.Mode < 2)
        {
            return null;
        }

        var written = threshold.Mode == 2 || modified.Count == 0
            ? null
            : relative ? modified[threshold.Change % modified.Count].Value.Relative : modified[threshold.Change % modified.Count].Value.Absolute;
        if (!decimal.TryParse(written, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return FixedValues[threshold.Fixed];
        }

        try
        {
            // One unit in the last place the value is written to, above or below it.
            var moved = Math.Abs(value) + (threshold.Offset * new decimal(1, 0, 0, false, value.Scale));
            return moved < 0 ? 0 : moved;
        }
        catch (OverflowException)
        {
            return Math.Abs(value);
        }
    }
}

internal static class VerdictInputs
{
    private const string BaselineDocumentId = "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string BaselineEvidenceId = "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
    private const string CandidateEvidenceId = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly BigInteger LargestSafeInteger = 9007199254740991;

    private static readonly string[][] Names = [["a"], ["b"], ["a/b"], ["~x"], ["value"], ["g", "a"], ["g", "b"]];

    // Values at the edges of a decimal: its largest value, half of it, its smallest step, a value
    // of 29 digits, and thirds, whose ratios do not end.
    private static readonly string[] Edges =
    [
        "79228162514264337593543950335", "79228162514264337593543950334", "7922816251426433759354395033",
        "39614081257132168796771975168", "0.0000000000000000000000000001", "0.0000000000000000000000000002",
        "7.9228162514264337593543950335", "1000000000000000000000000000.5", "0.3333333333333333333333333333",
        "3", "7", "9007199254740991", "9007199254740992", "1000000", "0.000001",
    ];

    // Values a decimal would round, or cannot hold at all.
    private static readonly string[] NotHeld =
    [
        "0.00000000000000000000000000001", "1.00000000000000000000000000001", "1.00000000000000000000000000002",
        "79228162514264337593543950335.4", "79228162514264337593543950336", "123456789012345678901234567890",
        "0.33333333333333333333333333333",
    ];

    private static readonly string[] Malformed = ["1e2", " 5", "5 ", "1,000", "", "abc", "0x1F", "--1", "1.2.3", "NaN", "Infinity", "-", "."];

    private static readonly string[] Steps =
    [
        "1", "-1", "0.5", "-0.5", "5", "6", "-6", "20", "21", "0.1",
        "0.0000000000000000000000000001", "-0.0000000000000000000000000001", "0.00000000000000000000000000001",
    ];

    private static readonly string?[] Units = [null, null, "ms", "s"];

    private static readonly Gen<string> Value = Gen.Frequency(
        (12, Gen.Select(Gen.Int[-30, 30], Gen.OneOfConst(string.Empty, string.Empty, ".5", ".25", ".125"), (whole, fraction) => whole.ToString(CultureInfo.InvariantCulture) + fraction)),
        (3, Gen.Const("0")),
        (5, Gen.Select(Gen.OneOfConst(Edges), Gen.Bool, (edge, negative) => negative ? "-" + edge : edge)),
        (1, Gen.Select(Gen.OneOfConst(NotHeld), Gen.Bool, (edge, negative) => negative ? "-" + edge : edge)),
        (1, Gen.OneOfConst(Malformed)));

    private static readonly Gen<Metric> Measurement = Gen.Select(Gen.Int[0, 6], Value, Value, Gen.Int[0, 12], Gen.Int, (name, value, other, relation, seed) =>
    {
        var random = new Random(seed);
        var unit = Units[random.Next(Units.Length)];
        var before = Read(value, unit, random);
        var after = relation switch
        {
            < 3 => Read(value, unit, random),
            < 7 => Read(Near(value, random) ?? other, unit, random),
            7 => Read(Scaled(value, random) ?? other, unit, random),
            8 => Read(other, unit, random),
            9 => Read(Near(value, random) ?? other, unit == "ms" ? "s" : unit is null ? "ms" : null, random),
            _ => null,
        };
        return relation switch
        {
            10 => new Metric(Names[name], before, null),
            11 => new Metric(Names[name], null, before),
            12 => new Metric(Names[name], null, null),
            _ => new Metric(Names[name], before, after),
        };
    });

    public static readonly Gen<VerdictCase> Case = Gen.Select(Measurement.Array[1, 4], Gen.Int, (metrics, seed) =>
        new VerdictCase(metrics.DistinctBy(metric => metric.Path).ToList(), seed));

    public static readonly Gen<PolicyRecipe> Recipe =
        Gen.Select(Gen.Int[0, 5], Gen.Int[0, 7], Gen.Int[0, 3], Gen.Int[-1, 1]).Array[4].Select(thresholds => new PolicyRecipe(thresholds));

    private static readonly Gen<string?> WrittenValue =
        Gen.OneOfConst<string?>(null, "0", "0", "1", "-1", "5", "6", "25", "0.5", "-0.5", "0.1", "+3", ".5", "5.", "1e2", "abc", string.Empty);

    private static readonly Gen<DeltaChange> WrittenChange = Gen.Select(
        Gen.OneOfConst("modified", "modified", "modified", "modified", "modified", "added", "removed", "missing", "unavailable", "incompatible", "renamed"),
        WrittenValue,
        WrittenValue,
        WrittenValue,
        WrittenValue,
        Gen.OneOfConst<string?>(null, "defined", "undefined-zero-baseline", "undefined-zero-baseline", "other"),
        (kind, before, after, absolute, relative, relativeStatus) => new DeltaChange("/p", kind, new NumericDeltaValue(before, after, absolute, relative, relativeStatus), null));

    /// <summary>A delta no comparator wrote: any status, any kind of change, and values that may not parse.</summary>
    public static readonly Gen<DeltaDocument> WrittenDelta = Gen.Select(
        Gen.OneOfConst("changed", "changed", "changed", "changed", "changed", "changed", "unchanged", "inconclusive", "incompatible", "unsupported", "unavailable", "invalid", "mystery"),
        WrittenChange.Array[0, 4],
        Gen.Bool,
        (status, changes, message) => new DeltaDocument(
            StructuredNumericComparator.DeltaSchema,
            BaselineDocumentId,
            status,
            "recognized",
            BaselineDocumentId,
            CandidateEvidenceId,
            new ComparatorProvenance("properties", "1.0.0", BaselineDocumentId, "org.example.metrics", "1.0.0"),
            status == "unchanged" ? [] : changes.Select((change, index) => change with { Path = "/p" + index.ToString(CultureInfo.InvariantCulture) }).ToList(),
            new DeltaProvenance(BaselineEvidenceId, CandidateEvidenceId, new Dictionary<string, string>(StringComparer.Ordinal)),
            message ? "The comparison could not be made." : null));

    public static readonly Gen<List<DrawnFinding>> Findings = Gen.Select(
        Gen.OneOfConst("pass", "pass", "warn", "fail", "needs-review", "inconclusive", "changed", "something-new"),
        Gen.OneOfConst<string?>(null, "0", "0.5", "-0.5", "2", "-3", "abc"),
        (status, relative) => (status, relative)).Array[0, 6].Select(drawn =>
            drawn.Select((finding, index) => new DrawnFinding("f" + index.ToString(CultureInfo.InvariantCulture), finding.status, finding.relative)).ToList());

    public static RankedFinding Ranked(DrawnFinding finding) =>
        new(Attention.ForStatus(finding.Status), finding.Status, finding.Id, "A finding.", null, null, null, finding.Relative);

    public static DeltaDocument Compare(string baseline, string candidate) => StructuredNumericComparator.Compare(
        Evidence(baseline, BaselineEvidenceId),
        Evidence(candidate, CandidateEvidenceId),
        new BaselineEvidenceReference(BaselineDocumentId, BaselineEvidenceId, "trusted"));

    /// <summary>
    /// The policy with one threshold raised or removed, or null when that would leave no threshold.
    /// </summary>
    public static NumericPolicy? Loosen(NumericPolicy policy, int which, int how)
    {
        decimal?[] values = [policy.WarnAboveAbsoluteIncrease, policy.FailAboveAbsoluteIncrease, policy.WarnAboveRelativeIncrease, policy.FailAboveRelativeIncrease];
        if (values.All(value => value is null) || values[which] is not { } current)
        {
            return null;
        }

        try
        {
            values[which] = how switch
            {
                0 => null,
                1 => current + new decimal(1, 0, 0, false, current.Scale),
                2 => current + 1,
                3 => current * 2,
                _ => decimal.MaxValue,
            };
        }
        catch (OverflowException)
        {
            values[which] = decimal.MaxValue;
        }

        return values.All(value => value is null)
            ? null
            : policy with
            {
                WarnAboveAbsoluteIncrease = values[0],
                FailAboveAbsoluteIncrease = values[1],
                WarnAboveRelativeIncrease = values[2],
                FailAboveRelativeIncrease = values[3],
            };
    }

    /// <summary>One side's payload, with its members in an order drawn from <paramref name="random"/>.</summary>
    public static string Render(IReadOnlyList<Metric> metrics, Func<Metric, Reading?> side, Random random)
    {
        var members = new List<string>();
        foreach (var top in metrics.Where(metric => metric.Name.Count == 1 && side(metric) is not null))
        {
            members.Add($"\"{top.Name[0]}\":{Write(side(top)!)}");
        }

        var grouped = metrics.Where(metric => metric.Name.Count == 2 && side(metric) is not null).ToList();
        if (grouped.Count > 0)
        {
            var inner = grouped.Select(metric => $"\"{metric.Name[1]}\":{Write(side(metric)!)}").ToArray();
            random.Shuffle(inner);
            members.Add($"\"g\":{{{string.Join(",", inner)}}}");
        }

        var all = members.ToArray();
        random.Shuffle(all);
        return "{" + string.Join(",", all) + "}";
    }

    public static string Print(VerdictCase input) => input.Baseline + "  ->  " + input.Candidate;

    public static string Print(DeltaDocument delta) => delta.Status + ": " + string.Join("; ", delta.Changes.Select(change =>
        $"{change.Path} {change.Kind} before={Show(change.Value.Before)} after={Show(change.Value.After)} absolute={Show(change.Value.Absolute)} relative={Show(change.Value.Relative)} ({Show(change.Value.RelativeStatus)})"));

    private static string Show(string? text) => text is null ? "null" : "'" + text + "'";

    private static string Write(Reading reading) => reading.Form switch
    {
        ValueForm.Bare => reading.Value,
        ValueForm.Number => "{\"value\":" + reading.Value + Unit(reading) + "}",
        _ => "{\"value\":\"" + reading.Value + "\"" + Unit(reading) + "}",
    };

    private static string Unit(Reading reading) => reading.Unit is null ? string.Empty : ",\"unit\":\"" + reading.Unit + "\"";

    // A whole number that canonical JSON admits may be a number token. Anything else is a string,
    // in one of the spellings a decimal string may have.
    private static Reading Read(string value, string? unit, Random random)
    {
        if (Exact.Parse(value) is not { } exact)
        {
            return new Reading(value, ValueForm.Text, unit);
        }

        if ((exact % Exact.One).IsZero && BigInteger.Abs(exact) <= LargestSafeInteger * Exact.One && random.Next(3) == 0)
        {
            var token = Exact.Spell(exact);
            return unit is null && random.Next(2) == 0 ? new Reading(token, ValueForm.Bare, null) : new Reading(token, ValueForm.Number, unit);
        }

        return new Reading(Respell(value, random), ValueForm.Text, unit);
    }

    private static string Respell(string value, Random random)
    {
        var negative = value.StartsWith('-');
        var digits = negative ? value[1..] : value;
        if (random.Next(4) == 0)
        {
            digits = (digits.Contains('.', StringComparison.Ordinal) ? digits : digits + ".") + new string('0', random.Next(0, 31));
        }

        digits = random.Next(6) switch
        {
            0 => new string('0', random.Next(1, 3)) + digits,
            1 when digits.StartsWith("0.", StringComparison.Ordinal) && digits.Length > 2 => digits[1..],
            _ => digits,
        };
        return (negative ? "-" : random.Next(6) == 0 ? "+" : string.Empty) + digits;
    }

    private static string? Near(string value, Random random) =>
        Exact.Parse(value) is { } exact ? Exact.Spell(exact + Exact.Parse(Steps[random.Next(Steps.Length)])!.Value) : null;

    private static string? Scaled(string value, Random random)
    {
        if (Exact.Parse(value) is not { } exact)
        {
            return null;
        }

        // Twice, a tenth more, half as much again, the other sign, and a third more.
        var (multiply, divide) = random.Next(5) switch { 0 => (2, 1), 1 => (11, 10), 2 => (3, 2), 3 => (-1, 1), _ => (4, 3) };
        var scaled = exact * multiply / divide;
        return Exact.Spell(scaled - (scaled % BigInteger.Pow(10, Exact.Places - 30)));
    }

    private static NumericEvidence Evidence(string payload, string evidenceId)
    {
        using var document = JsonDocument.Parse(payload);
        return new NumericEvidence(evidenceId, "org.example.metrics", "1.0.0", "all", "properties", document.RootElement.Clone());
    }
}

/// <summary>What the model expects of a diff: a status, or null when either answer is right.</summary>
internal sealed record ExpectedDiff(string? Status, IReadOnlyList<ExpectedChange> Changes);

internal sealed record ExpectedChange(string Path, string Kind, BigInteger? Before, BigInteger? After, string? Unit);

/// <summary>
/// The reference model. <see cref="Diff"/> follows the table under <c>diff</c> in
/// <c>docs/cli.md</c> with exact arithmetic. <see cref="Status"/> follows the rules under
/// <c>eval</c> there. Neither reads the implementation's numbers: every value is parsed again here.
/// </summary>
internal static class VerdictModel
{
    private static readonly string[] Worst = ["fail", "inconclusive", "needs-review", "warn", "pass"];

    // A result this close to the largest decimal may overflow or not, depending on rounding.
    private static readonly BigInteger Margin = BigInteger.Pow(10, 20);

    public static ExpectedDiff Diff(IReadOnlyList<Metric> metrics)
    {
        // A value that is not a decimal, or that a decimal cannot hold exactly, cannot be compared.
        if (metrics.Any(metric => (metric.Before is not null && Held(metric.Before) is null) || (metric.After is not null && Held(metric.After) is null)))
        {
            return new ExpectedDiff("inconclusive", []);
        }

        var changes = new List<ExpectedChange>();
        foreach (var metric in metrics.OrderBy(metric => metric.Path, StringComparer.Ordinal))
        {
            var before = metric.Before is null ? null : Held(metric.Before);
            var after = metric.After is null ? null : Held(metric.After);
            if (before is null && after is null)
            {
                continue;
            }

            if (before is null || after is null)
            {
                changes.Add(new ExpectedChange(metric.Path, before is null ? "added" : "removed", before, after, (metric.Before ?? metric.After)!.Unit));
                continue;
            }

            if (metric.Before!.Unit != metric.After!.Unit)
            {
                return new ExpectedDiff("incompatible", []);
            }

            if (before == after)
            {
                continue;
            }

            // The difference, and its ratio to the baseline, must each fit in a decimal.
            var difference = BigInteger.Abs(after.Value - before.Value);
            var baseline = BigInteger.Abs(before.Value);
            var limit = Exact.LargestDecimal * Exact.One;
            if (difference * Margin > limit * (Margin + 1) || (!baseline.IsZero && difference * Margin > baseline * Exact.LargestDecimal * (Margin + 1)))
            {
                return new ExpectedDiff("inconclusive", []);
            }

            if (difference * Margin > limit * (Margin - 1) || (!baseline.IsZero && difference * Margin > baseline * Exact.LargestDecimal * (Margin - 1)))
            {
                return new ExpectedDiff(null, []);
            }

            changes.Add(new ExpectedChange(metric.Path, "modified", before, after, metric.Before.Unit));
        }

        return new ExpectedDiff(changes.Count == 0 ? "unchanged" : "changed", changes);
    }

    public static void Check(ExpectedDiff expected, DeltaDocument delta)
    {
        if (expected.Status is null)
        {
            return;
        }

        Assert.Equal(expected.Status, delta.Status, "delta status");
        Assert.Equal(
            string.Join(" ", expected.Changes.Select(change => change.Path + ":" + change.Kind + ":" + change.Unit)),
            string.Join(" ", delta.Changes.Select(change => change.Path + ":" + change.Kind + ":" + change.Unit)),
            "changes");
        foreach (var (change, actual) in expected.Changes.Zip(delta.Changes))
        {
            var value = actual.Value;
            Assert.Equal(Text(change.Before), Text(value.Before), $"before at {change.Path}");
            Assert.Equal(Text(change.After), Text(value.After), $"after at {change.Path}");
            if (change.Kind != "modified")
            {
                Same(true, value.Absolute is null && value.Relative is null && value.RelativeStatus is null, $"an {change.Kind} value has no delta at {change.Path}");
                continue;
            }

            var difference = change.After!.Value - change.Before!.Value;
            var absolute = Canonical(value.Absolute, $"absolute at {change.Path}");
            if (Exact.IsHeld(difference))
            {
                Assert.Equal(Exact.Spell(difference), value.Absolute!, $"absolute at {change.Path}");
            }
            else
            {
                // A decimal keeps at least 28 significant digits of a difference it cannot hold.
                Same(true, BigInteger.Abs(absolute - difference) * BigInteger.Pow(10, 27) <= BigInteger.Abs(difference), $"absolute at {change.Path} is {value.Absolute}, not close to {Exact.Spell(difference)}");
            }

            Same(difference.Sign, absolute.Sign, $"the sign of absolute at {change.Path}");
            if (change.Before.Value.IsZero)
            {
                Assert.Equal("null undefined-zero-baseline", Text(value.Relative) + " " + value.RelativeStatus, $"relative at {change.Path}");
                continue;
            }

            Assert.Equal("defined", value.RelativeStatus ?? "null", $"relative status at {change.Path}");
            var relative = Canonical(value.Relative, $"relative at {change.Path}");

            // relative * before is within rounding of absolute: 28 places, or 28 significant digits.
            var error = BigInteger.Abs((relative * change.Before.Value) - (absolute * Exact.One));
            var allowed = BigInteger.Abs(change.Before.Value) * BigInteger.Max(BigInteger.Pow(10, Exact.Places - 28), BigInteger.Abs(relative) / BigInteger.Pow(10, 27));
            Same(true, error <= allowed, $"relative at {change.Path} is {value.Relative}, not {value.Absolute} divided by {value.Before}");
        }
    }

    /// <summary>The status the rules under <c>eval</c> in <c>docs/cli.md</c> give one change.</summary>
    public static string Status(DeltaChange change, NumericPolicy policy)
    {
        // "An added or removed measurement is always needs-review." A change that is not a
        // measured value at all is treated the same way.
        if (change.Kind is "added" or "removed" or "missing" or "unavailable" or "incompatible")
        {
            return "needs-review";
        }

        // A change that cannot be read is never a pass.
        var before = Exact.Parse(change.Value.Before);
        var absolute = Exact.Parse(change.Value.Absolute);
        var relative = Exact.Parse(change.Value.Relative);
        if (change.Kind != "modified" || before is null || absolute is null || Exact.Parse(change.Value.After) is null
            || (change.Value.Relative is not null && relative is null)
            || (change.Value.Relative is null && (!before.Value.IsZero || change.Value.RelativeStatus != "undefined-zero-baseline")))
        {
            return "inconclusive";
        }

        // "A decrease, or no change, passes."
        if (absolute.Value.Sign <= 0)
        {
            return "pass";
        }

        // "An increase strictly above a fail threshold fails." The absolute threshold is decided
        // first: it needs no relative value, so it also holds when the baseline was zero.
        if (Above(absolute, policy.FailAboveAbsoluteIncrease))
        {
            return "fail";
        }

        // "If a relative threshold is set and the baseline value was zero, ... needs-review."
        if (relative is null && (policy.WarnAboveRelativeIncrease ?? policy.FailAboveRelativeIncrease) is not null)
        {
            return "needs-review";
        }

        if (Above(relative, policy.FailAboveRelativeIncrease))
        {
            return "fail";
        }

        // "Otherwise, strictly above a warn threshold warns."
        return Above(absolute, policy.WarnAboveAbsoluteIncrease) || Above(relative, policy.WarnAboveRelativeIncrease) ? "warn" : "pass";
    }

    public static void CheckAssessment(DeltaDocument delta, NumericPolicy policy, AssessmentDocument assessment)
    {
        var expected = delta.Status switch
        {
            "unchanged" => ["numeric-policy pass"],
            "changed" => delta.Changes.Select(change => $"numeric-policy:{change.Path} {Status(change, policy)}").ToList(),
            _ => new List<string> { (delta.Status is "unavailable" or "invalid" or "incompatible" or "inconclusive" or "unsupported" ? "comparison-not-assessable" : "unknown-delta-status") + " inconclusive" },
        };
        Assert.Equal(string.Join("; ", expected), string.Join("; ", assessment.Findings.Select(finding => finding.Id + " " + finding.Status)), "findings");

        // "The Assessment's outcome is the worst finding, in the order fail, inconclusive,
        // needs-review, warn, pass."
        var worst = Worst.FirstOrDefault(status => expected.Exists(finding => finding.EndsWith(" " + status, StringComparison.Ordinal))) ?? "pass";
        Assert.Equal(worst, assessment.Outcome, "outcome");
        Assert.Equal(delta.Id + " " + delta.Status, assessment.DeltaId + " " + assessment.DeltaStatus, "the delta the assessment names");
    }

    /// <summary>"At least one threshold is required."</summary>
    public static void Refuses(DeltaDocument delta, NumericPolicy policy)
    {
        try
        {
            AssessmentEvaluator.Evaluate(delta, policy);
        }
        catch (ArgumentException)
        {
            return;
        }

        Assert.Fail("a policy with no threshold was evaluated");
    }

    /// <summary>
    /// The decision points a change reaches, for the report. The last three are the ones the owner
    /// may want to rule on: the model follows the implementation there, and says so.
    /// </summary>
    public static IEnumerable<string> DecisionPoints(DeltaChange change, NumericPolicy policy)
    {
        if (change.Kind != "modified" || Exact.Parse(change.Value.Absolute) is not { Sign: > 0 } absolute || Exact.Parse(change.Value.Before) is not { } before)
        {
            yield break;
        }

        var relative = Exact.Parse(change.Value.Relative);
        decimal?[] thresholds = [policy.WarnAboveAbsoluteIncrease, policy.FailAboveAbsoluteIncrease, policy.WarnAboveRelativeIncrease, policy.FailAboveRelativeIncrease];
        if (thresholds.Take(2).Any(threshold => threshold is { } value && Of(value) == absolute)
            || thresholds.Skip(2).Any(threshold => threshold is { } value && relative == Of(value)))
        {
            yield return "an increase exactly at a threshold";
        }

        var relativeSet = thresholds[2] is not null || thresholds[3] is not null;
        if (before.IsZero && relativeSet && Above(absolute, policy.FailAboveAbsoluteIncrease))
        {
            yield return "zero baseline over the absolute fail threshold (fails)";
        }

        if (before.Sign < 0 && relativeSet)
        {
            yield return "increase from a negative baseline with a relative threshold (relative is negative)";
        }

        // The ratio is rounded before it is compared, so one a hair above a threshold is not above it.
        if (before.Sign > 0 && relative is not null
            && thresholds.Skip(2).Any(threshold => threshold is { } value && relative <= Of(value) && absolute * Exact.One > Of(value) * before))
        {
            yield return "ratio above a relative threshold before rounding, not after";
        }
    }

    public static void CheckMirror(DeltaDocument forward, DeltaDocument backward)
    {
        if (forward.Status is not ("changed" or "unchanged") || backward.Status is not ("changed" or "unchanged"))
        {
            // Only the ratio to the baseline depends on the direction, so only an overflow may differ.
            Same(
                true,
                forward.Status == backward.Status || Overflowed(forward) || Overflowed(backward),
                $"the statuses are {forward.Status} and {backward.Status}");
            return;
        }

        Assert.Equal(
            string.Join("; ", forward.Changes.Select(change => $"{change.Path} {change.Kind} {Text(change.Value.Before)} {Text(change.Value.After)} {Text(change.Value.Absolute)} {change.Unit}")),
            string.Join("; ", backward.Changes.Select(change =>
                $"{change.Path} {(change.Kind == "added" ? "removed" : change.Kind == "removed" ? "added" : change.Kind)} {Text(change.Value.After)} {Text(change.Value.Before)} {Negated(change.Value.Absolute)} {change.Unit}")),
            "the changes in the other direction");
    }

    public static void CheckNotWorse(AssessmentDocument strict, AssessmentDocument loose)
    {
        Same(strict.Findings.Count, loose.Findings.Count, "the number of findings");
        foreach (var (before, after) in strict.Findings.Zip(loose.Findings))
        {
            Same(true, Array.IndexOf(Worst, after.Status) >= Array.IndexOf(Worst, before.Status), $"{before.Id} went from {before.Status} to {after.Status}");
        }

        Same(true, Array.IndexOf(Worst, loose.Outcome) >= Array.IndexOf(Worst, strict.Outcome), $"the outcome went from {strict.Outcome} to {loose.Outcome}");
    }

    /// <summary>
    /// The documented order: attention level, then rule severity, then the size of the relative
    /// change with an undefined one first, then the ID. Written as a key that sorts as text.
    /// </summary>
    public static string RankKey(DrawnFinding finding)
    {
        var level = finding.Status switch { "fail" => 0, "pass" => 2, _ => 1 };
        var severity = finding.Status switch { "fail" => 0, "inconclusive" => 1, "needs-review" => 2, "warn" => 3, "changed" => 4, _ => 5 };
        var size = finding.Relative is null ? "0" : "1" + (BigInteger.Pow(10, 80) - BigInteger.Abs(Exact.Parse(finding.Relative) ?? BigInteger.Zero)).ToString("D90", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture, $"{level}{severity}{size} {finding.Id}");
    }

    public static void Same<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Assert.Fail($"{message}: expected {expected}, got {actual}");
        }
    }

    private static bool Overflowed(DeltaDocument delta) => delta.Message?.Contains("overflowed", StringComparison.Ordinal) == true;

    private static bool Above(BigInteger? value, decimal? threshold) => value is { } increase && threshold is { } limit && increase > Of(limit);

    private static BigInteger Of(decimal value) => Exact.Parse(value.ToString(CultureInfo.InvariantCulture))!.Value;

    private static BigInteger? Held(Reading reading) =>
        Exact.Parse(reading.Value) is { } value && (reading.Form != ValueForm.Text || Exact.IsHeld(value)) ? value : null;

    private static BigInteger Canonical(string? text, string what)
    {
        if (text is null || !Exact.IsCanonical(text))
        {
            Assert.Fail($"{what} is {text ?? "null"}, which is not the one spelling of a decimal");
        }

        return Exact.Parse(text)!.Value;
    }

    private static string Text(BigInteger? value) => value is { } held ? Exact.Spell(held) : "null";

    private static string Text(string? value) => value ?? "null";

    private static string Negated(string? value) => value is null ? "null" : Exact.Spell(-Exact.Parse(value)!.Value);
}
