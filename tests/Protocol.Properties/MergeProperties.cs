using System.Globalization;
using System.Text.RegularExpressions;
using CsCheck;

namespace Diffra.Protocol.Properties;

/// <summary>
/// <see cref="EntryMerge.Merge"/> against the merge rules in <c>docs/record.md</c>. A wrong merge
/// drops or picks a value without a word, and its input is whatever other jobs wrote.
/// </summary>
internal sealed class MergeProperties : IPropertySuite
{
    public void Register(Runner runner)
    {
        long kept = 0, conflicted = 0, blocked = 0, superseded = 0, replaced = 0;

        runner.Check(
            "merge: the result follows the documented rules, with every pair of entries compared",
            Gen.Select(MergeInputs.Entries, MergeInputs.Plans),
            input =>
            {
                var entries = MergeInputs.Parse(input.Item1);
                var expected = MergeModel.Expect(Entries.Commit, entries, input.Item2);
                var actual = EntryMerge.Merge(Entries.Commit, entries, input.Item2);
                Count(ref kept, expected.Kept > 0);
                Count(ref conflicted, expected.Findings[MergeFindingKind.Conflict] > 0);
                Count(ref blocked, expected.Findings[MergeFindingKind.StatusConflict] + expected.Findings[MergeFindingKind.ValueAndData] > 0);
                Count(ref superseded, expected.Superseded.Count > 0);
                Count(ref replaced, expected.Replaced);
                Assert.Equal(string.Join("\n", expected.Entries), string.Join("\n", actual.Entries.Select(entry => entry.Json)), "merged entries differ");
                Assert.Equal(string.Join("\n", expected.Superseded), string.Join("\n", actual.SupersededAttempts), "superseded attempts differ");
                Assert.Equal(
                    string.Join("\n", expected.Named.Order(StringComparer.Ordinal)),
                    string.Join("\n", actual.Findings.Select(MergeModel.Named).Order(StringComparer.Ordinal)),
                    "findings differ");
            },
            input => MergeInputs.Print(input.Item1, input.Item2));

        runner.Check(
            "merge: the result does not depend on the order the jobs' files are read in",
            Gen.Select(MergeInputs.Entries, MergeInputs.Plans, Gen.Int),
            input =>
            {
                var entries = MergeInputs.Parse(input.Item1);
                var direct = EntryMerge.Merge(Entries.Commit, entries, input.Item2);
                var shuffled = EntryMerge.Merge(Entries.Commit, MergeInputs.Interleave(entries, new Random(input.Item3)), input.Item2);
                Assert.Equal(MergeInputs.Render(direct), MergeInputs.Render(shuffled), "merge result differs");
            },
            input => MergeInputs.Print(input.Item1, input.Item2));

        runner.Check(
            "merge: merging a merged file again changes nothing and finds no conflict",
            MergeInputs.Entries,
            input =>
            {
                var once = EntryMerge.Merge(Entries.Commit, MergeInputs.Parse(input), []);
                var twice = EntryMerge.Merge(Entries.Commit, once.Entries, []);
                Assert.Equal(
                    string.Join("\n", once.Entries.Select(entry => entry.Json)),
                    string.Join("\n", twice.Entries.Select(entry => entry.Json)),
                    "merged entries differ");
                Assert.Equal(
                    string.Join("\n", once.Findings.Where(finding => finding.Kind == MergeFindingKind.Rejected)),
                    string.Join("\n", twice.Findings),
                    "the second merge reports something other than the rejected entries");
                if (twice.SupersededAttempts.Count > 0)
                {
                    Assert.Fail("the second merge superseded " + string.Join(", ", twice.SupersededAttempts));
                }
            },
            input => MergeInputs.Print(input, []));

        runner.Check(
            "merge: an entry for another commit adds one finding and changes nothing else",
            Gen.Select(MergeInputs.Entries, MergeInputs.Plans, MergeInputs.Entry, Gen.Int),
            input =>
            {
                var entries = MergeInputs.Parse(input.Item1);
                var stray = RecordEntries.Parse(MergeInputs.Json(input.Item3 with { OtherCommit = true }));
                var with = entries.ToList();
                with.Insert(new Random(input.Item4).Next(entries.Count + 1), stray);
                var before = EntryMerge.Merge(Entries.Commit, entries, input.Item2);
                var after = EntryMerge.Merge(Entries.Commit, with, input.Item2);
                var added = after.Findings.Where(finding => finding.Kind == MergeFindingKind.OtherCommit).ToList();
                foreach (var finding in before.Findings.Where(finding => finding.Kind == MergeFindingKind.OtherCommit))
                {
                    added.Remove(finding);
                }

                if (added.Count != 1)
                {
                    Assert.Fail($"expected one more finding for another commit, got {added.Count}");
                }

                var rest = after with { Findings = after.Findings.Where(finding => !ReferenceEquals(finding, added[0])).ToList() };
                Assert.Equal(MergeInputs.Render(before), MergeInputs.Render(rest), "merge result differs");
            },
            input => MergeInputs.Print(input.Item1, input.Item2) + "\n  stray: " + MergeInputs.Json(input.Item3 with { OtherCommit = true }));

        runner.Check(
            "merge: respelling data changes no finding and no choice of entry",
            Gen.Select(MergeInputs.Entries, MergeInputs.Plans, Gen.Int),
            input =>
            {
                // Data holding an exponent the runtime cannot compare is identical only as written
                // (see RecordEntries.AreIdentical), so it keeps its spelling here.
                var respelt = input.Item1
                    .Select((entry, index) => entry.Data == MergeInputs.AsWritten ? entry : entry with { Spelling = unchecked(input.Item3 + index) })
                    .ToArray();
                var before = EntryMerge.Merge(Entries.Commit, MergeInputs.Parse(input.Item1), input.Item2);
                var after = EntryMerge.Merge(Entries.Commit, MergeInputs.Parse(respelt), input.Item2);
                Assert.Equal(MergeInputs.Render(before, withData: false), MergeInputs.Render(after, withData: false), "merge result differs");
            },
            input => MergeInputs.Print(input.Item1, input.Item2));

        runner.Note(() => string.Create(
            CultureInfo.InvariantCulture,
            $"Merges against the model: {kept} kept an entry, {conflicted} had a conflict, {blocked} had a dataset given two ways or with two statuses, {superseded} superseded an attempt, {replaced} used --replace."));
    }

    private static void Count(ref long counter, bool reached)
    {
        if (reached)
        {
            Interlocked.Increment(ref counter);
        }
    }
}

internal enum EntryKind
{
    Value,
    Data,
    Skipped,
    Failed,
    Rejected,
}

/// <summary>What one generated entry says. <see cref="Spelling"/> picks how its data is written.</summary>
internal sealed record EntrySpec(
    string Job,
    int Attempt,
    string Project,
    string Dataset,
    int Dimensions,
    EntryKind Kind,
    string? Key,
    string Value,
    string? Unit,
    bool Replaces,
    int Data,
    int Spelling,
    bool OtherCommit);

/// <summary>Entries over a small alphabet, so that jobs, attempts, identities and values collide often.</summary>
internal static class MergeInputs
{
    private static readonly (string Name, string Value)[][] DimensionSets =
    [
        [],
        [("os", "linux")],
        [("os", "mac")],
        [("arch", "x64"), ("os", "linux")],
        [("arch", "x64"), ("os", "mac")],
    ];

    // Data to respell: two values that differ, one holding an exponent the runtime cannot compare,
    // and one holding zero and a negative fraction.
    private static readonly J[] DataPool =
    [
        new JObj([("n", new JNum("1")), ("m", new JArr([new JNum("250"), new JStr("x")]))]),
        new JObj([("n", new JNum("2")), ("m", new JArr([new JNum("250"), new JStr("x")]))]),
        new JObj([("n", new JNum("1")), ("m", new JNum("1e4000000000"))]),
        new JObj([("n", new JNum("0")), ("m", new JNum("-1.5"))]),
    ];

    private static readonly Func<string?> NoText = () => null;

    /// <summary>The index in the data pool of the data that is compared as written.</summary>
    public const int AsWritten = 2;

    public static readonly Gen<EntrySpec> Entry =
        from job in Gen.Frequency((9, Gen.OneOfConst("a", "b", "c")), (1, Gen.Const("B")))
        from attempt in Gen.Frequency((10, Gen.Const(1)), (4, Gen.Const(2)), (2, Gen.Const(3)), (1, Gen.OneOfConst(9, 10, 11)))
        from project in Gen.Frequency((7, Gen.Const("main")), (1, Gen.Const("docs")))
        from dataset in Gen.OneOfConst("alpha", "beta")
        from dimensions in Gen.Frequency((3, Gen.Const(0)), (2, Gen.Int[1, DimensionSets.Length - 1]))
        from kind in Gen.Frequency(
            (5, Gen.Const(EntryKind.Value)),
            (4, Gen.Const(EntryKind.Data)),
            (1, Gen.Const(EntryKind.Skipped)),
            (1, Gen.Const(EntryKind.Failed)),
            (1, Gen.Const(EntryKind.Rejected)))
        from key in Gen.Frequency((3, Gen.Const(NoText)), (1, Gen.OneOfConst<string?>("k1", "k2")))
        from value in Gen.Frequency((3, Gen.Const("1")), (1, Gen.OneOfConst("2", "1.5", "-1")))
        from unit in Gen.Frequency((4, Gen.Const(NoText)), (1, Gen.OneOfConst<string?>("ms")))
        from replaces in Gen.Bool
        from data in Gen.Frequency((3, Gen.Const(0)), (1, Gen.Int[1, DataPool.Length - 1]))
        from spelling in Gen.Int
        from otherCommit in Gen.Frequency((15, Gen.Const(false)), (1, Gen.Const(true)))
        select new EntrySpec(job, attempt, project, dataset, dimensions, kind, key, value, unit, replaces, data, spelling, otherCommit);

    /// <summary>
    /// Up to ten entries. About one in four says something more about what an earlier entry of the
    /// same job and attempt spoke of, since that is where --replace and a conflict within a job arise.
    /// </summary>
    public static readonly Gen<EntrySpec[]> Entries = Gen.Select(Entry.Array[0, 10], Gen.Int, (entries, seed) =>
    {
        var random = new Random(seed);
        for (var index = 1; index < entries.Length; index++)
        {
            if (random.Next(4) == 0)
            {
                var earlier = entries[random.Next(index)];
                entries[index] = entries[index] with
                {
                    Job = earlier.Job,
                    Attempt = earlier.Attempt,
                    Project = earlier.Project,
                    Dataset = earlier.Dataset,
                    Dimensions = earlier.Dimensions,
                    Key = earlier.Key,
                };
            }
        }

        return entries;
    });

    public static readonly Gen<ExpectedEntry[]> Plans = Gen.Select(
            Gen.OneOfConst("alpha", "beta"),
            Gen.Int[0, DimensionSets.Length - 1],
            Gen.Frequency((7, Gen.Const("main")), (1, Gen.Const("docs"))))
        .Array[0, 3]
        .Select(plans => plans
            .Select(plan => new ExpectedEntry(
                plan.Item3,
                plan.Item1,
                DimensionSets[plan.Item2].ToDictionary(dimension => dimension.Name, dimension => dimension.Value, StringComparer.Ordinal)))
            .ToArray());

    public static string Json(EntrySpec spec)
    {
        var dimensions = DimensionSets[spec.Dimensions];
        var commit = spec.OtherCommit ? Properties.Entries.OtherCommit : Properties.Entries.Commit;
        return spec.Kind switch
        {
            EntryKind.Value => Properties.Entries.FromValue(spec.Job, spec.Attempt, spec.Dataset, dimensions, spec.Key, spec.Value, spec.Unit, spec.Replaces, commit, spec.Project),
            EntryKind.Data => Properties.Entries.FromData(
                spec.Job,
                spec.Attempt,
                spec.Dataset,
                dimensions,
                Properties.Json.Render(DataTrees.Respell(DataPool[spec.Data], new Random(spec.Spelling), keepValue: true)),
                commit,
                spec.Replaces,
                spec.Project),
            EntryKind.Rejected => Properties.Entries.Rejected(spec.Job, spec.Attempt, spec.Dataset, dimensions, spec.Value == "1" ? "ten" : "x", commit, spec.Project),
            _ => Properties.Entries.Unrecorded(spec.Job, spec.Attempt, spec.Dataset, dimensions, spec.Kind == EntryKind.Failed, "The collector did not run.", commit, spec.Project),
        };
    }

    public static List<RecordEntry> Parse(IEnumerable<EntrySpec> specs) => specs.Select(spec => RecordEntries.Parse(Json(spec))).ToList();

    /// <summary>
    /// The entries in another order that keeps the order within each job and attempt, as reading
    /// the jobs' files in another order would.
    /// </summary>
    public static List<RecordEntry> Interleave(IReadOnlyList<RecordEntry> entries, Random random)
    {
        var files = entries.GroupBy(entry => (entry.JobName, entry.Attempt)).Select(file => new Queue<RecordEntry>(file)).ToList();
        var result = new List<RecordEntry>(entries.Count);
        while (files.Count > 0)
        {
            var file = files[random.Next(files.Count)];
            result.Add(file.Dequeue());
            if (file.Count == 0)
            {
                files.Remove(file);
            }
        }

        return result;
    }

    /// <summary>
    /// The whole result as text. Without data, a recorded entry from files is named by its job,
    /// attempt and identity, so that two results that differ only in how data is written are equal.
    /// </summary>
    public static string Render(MergeResult result, bool withData = true) => string.Join("\n", (string[])
    [
        .. result.Entries.Select(entry => withData || entry.Data is null
            ? entry.Json
            : string.Create(CultureInfo.InvariantCulture, $"data from {entry.JobName} attempt {entry.Attempt} for {entry.Identity}")),
        "findings:",
        .. result.Findings.Select(finding => $"{finding.Kind}: {finding.Message}"),
        "superseded:",
        .. result.SupersededAttempts,
    ]);

    public static string Print(IEnumerable<EntrySpec> specs, IEnumerable<ExpectedEntry> plans) =>
        string.Join("\n", specs.Select(Json))
        + "\n  planned: "
        + string.Join("; ", plans.Select(plan => $"{plan.Project}/{plan.Dataset} {string.Join(",", plan.Dimensions.Select(dimension => $"{dimension.Key}={dimension.Value}"))}"));
}

/// <summary>What the model expects of a merge.</summary>
internal sealed record MergeExpectation(
    IReadOnlyList<string> Entries,
    int Kept,
    IReadOnlyList<string> Superseded,
    IReadOnlyDictionary<MergeFindingKind, int> Findings,
    IReadOnlyList<string> Named,
    bool Replaced);

/// <summary>
/// The merge rules of <c>docs/record.md</c>, written to be read against them rather than to be
/// fast: nothing is hashed, and every pair of entries for an identity is compared.
/// </summary>
/// <remarks>
/// Three things here are taken from the implementation and its comments, because rules 1 to 5 do
/// not state them, so this model cannot say whether they are right. A second entry in a job that
/// differs only in status or form is a conflict of values as well as a finding for the dataset.
/// A conflict within a job is reported once for an identity, naming the first job that has it.
/// A rejected entry is kept once per job, after the merged entries. The model also takes an
/// entry's identity and whether two entries are identical from <see cref="RecordEntries"/>.
/// </remarks>
internal static partial class MergeModel
{
    public static MergeExpectation Expect(string commit, IReadOnlyList<RecordEntry> all, IReadOnlyList<ExpectedEntry> plans)
    {
        var named = new List<(MergeFindingKind Kind, string Names)>();
        var mine = all.Where(entry => entry.Commit == commit).ToList();
        named.AddRange(all.Where(entry => entry.Commit != commit).Select(entry => (MergeFindingKind.OtherCommit, $"'{entry.JobName}' {entry.Commit}")));

        // Rule 1: only the last attempt of each job counts.
        var jobs = mine.Select(entry => entry.JobName).Distinct().Order(StringComparer.Ordinal).ToList();
        var superseded = new List<string>();
        var last = new List<RecordEntry>();
        foreach (var job in jobs)
        {
            var attempts = mine.Where(entry => entry.JobName == job).Select(entry => entry.Attempt).Distinct().Order().ToList();
            superseded.AddRange(attempts.SkipLast(1).Select(attempt => string.Create(CultureInfo.InvariantCulture, $"{job} attempt {attempt}")));
            last.AddRange(mine.Where(entry => entry.JobName == job && entry.Attempt == attempts[^1]));
        }

        // A rejected entry is kept as it is, once per job, and reported.
        var rejected = new List<string>();
        foreach (var job in jobs)
        {
            foreach (var entry in last.Where(entry => entry.JobName == job && entry.Status == EntryStatus.Rejected).DistinctBy(entry => entry.Json))
            {
                rejected.Add(entry.Json);
                named.Add((MergeFindingKind.Rejected, string.Create(CultureInfo.InvariantCulture, $"'{job}' attempt {entry.Attempt} is not a number")));
            }
        }

        var stated = last.Where(entry => entry.Status != EntryStatus.Rejected).ToList();

        // Rule 2: within a job, --replace replaces the earlier recorded entry. Any other second
        // entry must say the same as the one it follows, or the identity is in conflict (rule 4).
        var standing = new List<RecordEntry>();
        var conflictedWithin = new HashSet<string>(StringComparer.Ordinal);
        var replaced = false;
        foreach (var job in jobs)
        {
            foreach (var identity in stated.Where(entry => entry.JobName == job).Select(entry => entry.Identity).Distinct().ToList())
            {
                var given = stated.Where(entry => entry.JobName == job && entry.Identity == identity).ToList();
                var current = given[0];
                foreach (var next in given.Skip(1))
                {
                    if (next.Replaces && current.Status == EntryStatus.Recorded && next.Status == EntryStatus.Recorded)
                    {
                        current = next;
                        replaced = true;
                    }
                    else if (!RecordEntries.AreIdentical(current, next) && conflictedWithin.Add(identity))
                    {
                        named.Add((MergeFindingKind.Conflict, string.Create(CultureInfo.InvariantCulture, $"'{job}' attempt {next.Attempt}")));
                    }
                }

                standing.Add(current);
            }
        }

        // Rule 4, for a whole dataset and its dimensions: two statuses, or --value against files.
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dataset in stated.Select(DatasetOf).Distinct().ToList())
        {
            var given = stated.Where(entry => DatasetOf(entry) == dataset).ToList();
            var statuses = given.Select(entry => entry.Status).Distinct().Order().ToList();
            if (statuses.Count > 1)
            {
                // Each status, with the jobs that gave it.
                named.Add((MergeFindingKind.StatusConflict, string.Join(" ", statuses.Select(status => $"{RecordEntries.Token(status)} in {Jobs(given.Where(entry => entry.Status == status))}"))));
                blocked.Add(dataset);
            }

            if (given.Exists(entry => entry.Value is not null) && given.Exists(entry => entry.Data is not null))
            {
                named.Add((MergeFindingKind.ValueAndData, $"--value {Jobs(given.Where(entry => entry.Value is not null))} from files {Jobs(given.Where(entry => entry.Data is not null))}"));
                blocked.Add(dataset);
            }
        }

        // Rules 3 and 4, across jobs: the values given one way must all be identical.
        var kept = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var identity in standing.Select(entry => entry.Identity).Distinct().ToList())
        {
            var given = standing.Where(entry => entry.Identity == identity).ToList();
            var conflicts = new[] { given.FindAll(entry => entry.Value is not null), given.FindAll(entry => entry.Data is not null) }
                .Where(form => form.Exists(first => form.Exists(second => !RecordEntries.AreIdentical(first, second))))
                .Select(form => (MergeFindingKind.Conflict, string.Create(CultureInfo.InvariantCulture, $"{Different(form)} different {Jobs(form)}")))
                .ToList();
            named.AddRange(conflicts);
            if (conflicts.Count == 0 && !conflictedWithin.Contains(identity) && !blocked.Contains(DatasetOf(given[0])))
            {
                kept[identity] = given[0].Json;
            }
        }

        // Rule 5: a planned value with no entry, or with an entry that says the collector failed.
        foreach (var plan in plans)
        {
            var matching = stated.Where(entry => entry.Project == plan.Project
                && entry.Dataset == plan.Dataset
                && plan.Dimensions.All(dimension => entry.Dimensions.TryGetValue(dimension.Key, out var value) && value == dimension.Value)).ToList();
            if (matching.Count == 0)
            {
                named.Add((MergeFindingKind.NotCollected, string.Empty));
            }
            else if (matching.Exists(entry => entry.Status == EntryStatus.Failed))
            {
                named.Add((MergeFindingKind.CollectionFailed, $"failed in {Jobs(matching.Where(entry => entry.Status == EntryStatus.Failed))}"));
            }
        }

        var findings = Enum.GetValues<MergeFindingKind>().ToDictionary(kind => kind, kind => named.Count(finding => finding.Kind == kind));
        return new MergeExpectation(
            [.. kept.Values, .. rejected],
            kept.Count,
            superseded,
            findings,
            named.Select(finding => $"{finding.Kind}: {finding.Names}".TrimEnd()).ToList(),
            replaced);
    }

    /// <summary>
    /// What a finding names, read from its message: the jobs, each status or form they are named
    /// under, how many values differ, an attempt, another commit, and the problem of a rejected entry. The words between
    /// are not compared.
    /// </summary>
    public static string Named(MergeFinding finding) =>
        $"{finding.Kind}: {string.Join(" ", NamedParts().Matches(finding.Message).Select(match => match.Value))}".TrimEnd();

    // How many values there are, counting two entries that say the same thing once.
    private static int Different(List<RecordEntry> entries) =>
        entries.Where((entry, index) => !entries.Take(index).Any(earlier => RecordEntries.AreIdentical(earlier, entry))).Count();

    private static string Jobs(IEnumerable<RecordEntry> entries) =>
        string.Join(" ", entries.Select(entry => $"'{entry.JobName}'").Distinct().Order(StringComparer.Ordinal));

    [GeneratedRegex(@"[0-9]+ different|(?:recorded|skipped|failed) in|'(?:a|b|c|B)'|attempt [0-9]+|--value|from files|is not a number|\b[0-9a-f]{40}(?=,)")]
    private static partial Regex NamedParts();

    private static string DatasetOf(RecordEntry entry) =>
        $"{entry.Project}\n{entry.Dataset}\n{string.Join("\n", entry.Dimensions.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}\n{pair.Value}"))}";
}
