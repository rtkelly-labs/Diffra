using System.Globalization;
using System.Numerics;
using CsCheck;

namespace Diffra.Protocol.Properties;

/// <summary>
/// Number equality and entry equality, checked against exact arithmetic. Three pieces of code
/// decide whether two numbers are equal: the structural comparator, the runtime's deep comparison
/// behind <see cref="RecordEntries.AreIdentical"/>, and the hash the merge buckets entries by.
/// A false "equal" hides a change and a false "different" raises a conflict nobody caused.
/// </summary>
internal sealed class NumberProperties : IPropertySuite
{
    public void Register(Runner runner)
    {
        long equalPairs = 0, largePairs = 0, identicalData = 0, largeData = 0, conflicts = 0, agreements = 0;

        runner.Check(
            "numbers: the structural comparator finds two tokens equal exactly when their values are",
            Numbers.Pair,
            pair =>
            {
                var (first, second) = pair;
                var same = ExactNumber.Parse(first) == ExactNumber.Parse(second);
                var normalized = Normalizes(first) && Normalizes(second);
                var equal = Structural.Compare(first, second, ArrayComparisonMode.OrderedSequence).Count == 0;
                Count(ref equalPairs, same);
                Count(ref largePairs, !normalized);
                if (equal && !same)
                {
                    Assert.Fail("different values compare equal");
                }

                // An exponent beyond half the range of a long is compared as written, by design.
                if (same && !equal && (normalized || first == second))
                {
                    Assert.Fail("equal values compare different");
                }
            },
            pair => $"{pair.First}  vs  {pair.Second}");

        runner.Check(
            "entries: data is identical exactly when it is equal, whatever the spelling or member order",
            Gen.Select(DataTrees.Root, Gen.Int),
            input =>
            {
                var (first, second) = Respelt(input);
                var exact = DataTrees.Equal(first, second);
                var compared = DataTrees.NumberTokens(first).Concat(DataTrees.NumberTokens(second)).All(DeepCompares);
                var identical = RecordEntries.AreIdentical(Entry("a", first), Entry("b", second));
                Count(ref identicalData, exact);
                Count(ref largeData, !compared);
                if (identical && !exact)
                {
                    Assert.Fail("different data is identical");
                }

                // Data holding an exponent the runtime cannot compare is identical only as written, by design.
                if (exact && !identical && (compared || Json.Render(first) == Json.Render(second)))
                {
                    Assert.Fail("equal data is not identical");
                }
            },
            input => Print(Respelt(input)));

        runner.Check(
            "entries: AreIdentical is reflexive, symmetric and transitive",
            Gen.Select(DataTrees.Root, Gen.Int, Gen.Int),
            input =>
            {
                var (first, second, third) = Three(input);
                RecordEntry[] entries = [Entry("a", first), Entry("b", second), Entry("c", third)];
                var identical = new bool[3, 3];
                for (var row = 0; row < 3; row++)
                {
                    for (var column = 0; column < 3; column++)
                    {
                        identical[row, column] = RecordEntries.AreIdentical(entries[row], entries[column]);
                    }
                }

                for (var row = 0; row < 3; row++)
                {
                    if (!identical[row, row])
                    {
                        Assert.Fail($"entry {row} is not identical to itself");
                    }

                    for (var column = 0; column < 3; column++)
                    {
                        var other = 3 - row - column;
                        if (identical[row, column] != identical[column, row])
                        {
                            Assert.Fail($"entries {row} and {column} are identical in one direction only");
                        }

                        if (row != column && identical[row, column] && identical[column, other] && !identical[row, other])
                        {
                            Assert.Fail($"entries {row} and {column}, and {column} and {other}, are identical, but {row} and {other} are not");
                        }
                    }
                }
            },
            input => Print(Three(input)));

        runner.Check(
            "merge: jobs conflict exactly when AreIdentical finds their data different",
            Gen.Select(DataTrees.Root, Gen.Int, Gen.Int),
            input =>
            {
                var (first, second, third) = Three(input);
                RecordEntry[] entries = [Entry("a", first), Entry("b", second), Entry("c", third)];

                // The model: one class per entry that is identical to no earlier entry, every pair compared.
                var classes = Enumerable.Range(0, entries.Length)
                    .Count(index => !entries.Take(index).Any(earlier => RecordEntries.AreIdentical(earlier, entries[index])));
                var result = EntryMerge.Merge(Entries.Commit, entries, []);
                Count(ref conflicts, classes > 1);
                Count(ref agreements, classes == 1);
                var expected = classes == 1
                    ? string.Empty
                    : string.Create(CultureInfo.InvariantCulture, $"Conflict: main/tests has {classes} different values across jobs 'a', 'b' and 'c'.");
                Assert.Equal(expected, string.Join("\n", result.Findings.Select(finding => $"{finding.Kind}: {finding.Message}")), "findings differ");
                if (result.Entries.Count != (classes == 1 ? 1 : 0))
                {
                    Assert.Fail($"{classes} different values gave {result.Entries.Count} merged entries");
                }
            },
            input => Print(Three(input)));

        runner.Note(() => string.Create(
            CultureInfo.InvariantCulture,
            $"Number pairs: {equalPairs} equal in value, {largePairs} with an exponent the comparator does not normalize."));
        runner.Note(() => string.Create(
            CultureInfo.InvariantCulture,
            $"Entry data pairs: {identicalData} equal, {largeData} with an exponent the runtime cannot compare. Three-job merges: {agreements} agreed, {conflicts} conflicted."));
    }

    private static void Count(ref long counter, bool reached)
    {
        if (reached)
        {
            Interlocked.Increment(ref counter);
        }
    }

    private static bool Normalizes(string token) => BigInteger.Abs(Numbers.WrittenExponent(token)) <= Numbers.SafeForComparator;

    private static bool DeepCompares(string token) => BigInteger.Abs(Numbers.WrittenExponent(token)) <= Numbers.SafeForDeepEquals;

    private static RecordEntry Entry(string job, J data) =>
        RecordEntries.Parse(Entries.FromData(job, 1, "tests", [], Json.Render(data)));

    private static (J First, J Second) Respelt((J Tree, int Seed) input) =>
        (input.Tree, DataTrees.Respell(input.Tree, new Random(input.Seed)));

    // The third is derived from the second, so that a chain of two equalities is common.
    private static (J First, J Second, J Third) Three((J Tree, int SecondSeed, int ThirdSeed) input)
    {
        var second = DataTrees.Respell(input.Tree, new Random(input.SecondSeed));
        return (input.Tree, second, DataTrees.Respell(second, new Random(input.ThirdSeed)));
    }

    private static string Print((J First, J Second) pair) => $"{Json.Render(pair.First)}  vs  {Json.Render(pair.Second)}";

    private static string Print((J First, J Second, J Third) three) =>
        $"{Json.Render(three.First)}  vs  {Json.Render(three.Second)}  vs  {Json.Render(three.Third)}";
}

/// <summary>Entry data: small trees whose leaves are mostly numbers in unusual spellings.</summary>
internal static class DataTrees
{
    private static readonly Gen<J> Leaf = Gen.Frequency(
        (6, Numbers.Token.Select(token => (J)new JNum(token))),
        (1, Gen.OneOfConst("x", "y").Select(text => (J)new JStr(text))),
        (1, Gen.Bool.Select(value => (J)new JBool(value))),
        (1, Gen.Const<J>(new JNull())));

    /// <summary>An object, since an entry's data is an object or an array.</summary>
    public static readonly Gen<J> Root = Object(2, 1);

    /// <summary>
    /// The same tree written another way: object members in another order and every number respelt.
    /// One time in three, unless <paramref name="keepValue"/> is set, a single leaf is also varied,
    /// which changes its value about half the time.
    /// </summary>
    public static J Respell(J tree, Random random, bool keepValue = false)
    {
        var leaves = CountLeaves(tree);
        var changed = !keepValue && random.Next(3) == 0 && leaves > 0 ? random.Next(leaves) : -1;
        var index = 0;
        return Rewrite(tree);

        J Rewrite(J value)
        {
            switch (value)
            {
                case JObj obj:
                    var members = obj.Members.Select(member => (member.Name, Rewrite(member.Value))).ToArray();
                    random.Shuffle(members);
                    return new JObj(members);
                case JArr array:
                    return new JArr(array.Items.Select(Rewrite).ToArray());
                default:
                    var change = index++ == changed;
                    if (value is not JNum number)
                    {
                        return change ? new JStr("changed") : value;
                    }

                    var exact = ExactNumber.Parse(number.Token);
                    var parts = (exact.Negative, exact.Digits, exact.Exponent);
                    return new JNum(Numbers.Spell(change ? Change(parts) : parts, random));
            }
        }

        (bool Negative, string Digits, BigInteger Exponent) Change((bool Negative, string Digits, BigInteger Exponent) parts) =>
            Numbers.Vary(parts, (false, "7", BigInteger.Zero), random);
    }

    /// <summary>Whether two trees hold the same data: members in any order, numbers by exact value.</summary>
    public static bool Equal(J first, J second) => (first, second) switch
    {
        (JNum a, JNum b) => ExactNumber.Parse(a.Token) == ExactNumber.Parse(b.Token),
        (JArr a, JArr b) => a.Items.Count == b.Items.Count && a.Items.Zip(b.Items).All(pair => Equal(pair.First, pair.Second)),
        (JObj a, JObj b) => a.Members.Count == b.Members.Count && a.Members.All(member =>
            b.Members.Any(other => other.Name == member.Name && Equal(member.Value, other.Value))),
        (JObj or JArr or JNum, _) or (_, JObj or JArr or JNum) => false,
        _ => first == second,
    };

    /// <summary>Every number token in the tree.</summary>
    public static IEnumerable<string> NumberTokens(J tree) => tree switch
    {
        JNum number => [number.Token],
        JArr array => array.Items.SelectMany(NumberTokens),
        JObj obj => obj.Members.SelectMany(member => NumberTokens(member.Value)),
        _ => [],
    };

    private static int CountLeaves(J tree) => tree switch
    {
        JArr array => array.Items.Sum(CountLeaves),
        JObj obj => obj.Members.Sum(member => CountLeaves(member.Value)),
        _ => 1,
    };

    private static Gen<J> Value(int depth) => depth == 0
        ? Leaf
        : Gen.Frequency((4, Leaf), (1, Value(depth - 1).Array[0, 3].Select(items => (J)new JArr(items))), (1, Object(depth - 1, 0)));

    private static Gen<J> Object(int depth, int minimum) =>
        Gen.Select(Gen.OneOfConst("p", "q", "r"), Value(depth)).Array[minimum, 3].Select(members =>
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return (J)new JObj(members.Where(member => seen.Add(member.Item1)).ToArray());
        });
}
