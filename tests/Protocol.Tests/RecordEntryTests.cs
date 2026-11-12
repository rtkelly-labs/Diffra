using System.Text.Json;

namespace Diffra.Protocol.Tests;

/// <summary>
/// Checks record entries: the validator accepts every example in <c>examples/record/</c> and rejects
/// every file in <c>examples/record/invalid/</c>, the entries file appends and reads whole lines,
/// and the merge follows the proposed rules in <c>docs/record.md</c>.
/// </summary>
public static class RecordEntryTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string OtherCommit = "89abcdef0123456789abcdef0123456789abcdef";

    public static void RunAll()
    {
        AcceptsTheExamples();
        RejectsTheInvalidExamples();
        ReadsAnEntry();
        ReportsInvalidJson();
        CountsLengthsInCodePoints();
        MatchesTheSchemasPatternEngine();
        ReadsPastBadBytes();
        NamesEntriesFiles();
        AppendsAndReadsEntriesFiles();
        RefusesToAppendAnInvalidEntry();
        ReportsAnEntryForAnotherCommit();
        KeepsOnlyTheLatestAttempt();
        AppliesReplaceWithinAJob();
        KeepsIdenticalEntriesOnceFromTheFirstJob();
        ReportsDifferentValuesWithoutChoosing();
        ReportsRecordedAgainstSkipped();
        ReportsValueAgainstData();
        ReportsValueAgainstDataPastAConflict();
        ReportsWhatWasNotCollected();
        KeepsRejectedEntriesOutOfConflicts();
        Console.WriteLine("PASS record entries, entries files and the merge");
    }

    private static void AcceptsTheExamples()
    {
        var files = Directory.GetFiles(Path.Combine(FindRepositoryRoot(), "examples", "record"), "*.json");
        Equal(true, files.Length >= 8);
        foreach (var file in files)
        {
            var problems = RecordEntries.Validate(File.ReadAllText(file));
            if (problems.Count != 0)
            {
                throw new InvalidOperationException($"{Path.GetFileName(file)} should be valid, but: {string.Join(" ", problems)}");
            }
        }
    }

    private static void RejectsTheInvalidExamples()
    {
        var files = Directory.GetFiles(Path.Combine(FindRepositoryRoot(), "examples", "record", "invalid"), "*.json");
        Equal(true, files.Length >= 33);
        foreach (var file in files)
        {
            if (RecordEntries.Validate(File.ReadAllText(file)).Count == 0)
            {
                throw new InvalidOperationException($"{Path.GetFileName(file)} should be invalid.");
            }
        }
    }

    private static void ReadsAnEntry()
    {
        var entry = RecordEntries.Parse(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "examples", "record", "from-file.json")));
        Equal("main", entry.Project);
        Equal("tests", entry.Dataset);
        Equal("test (linux)", entry.JobName);
        Equal(1, entry.Attempt);
        Equal("linux", entry.Dimensions["os"]);
        Equal(EntryStatus.Recorded, entry.Status);
        Equal(true, entry.Data.HasValue);
        Equal(false, entry.Json.Contains('\n', StringComparison.Ordinal));

        var keyed = RecordEntries.Parse(Entry("pack", 1, value: "12.5", unit: "ms", key: "Suite.Case"));
        Equal(new EntryValue("12.5", "ms"), keyed.Value);
        Equal("Suite.Case", keyed.Key);
        Throws<JsonException>(() => RecordEntries.Parse("{}"));
    }

    private static void ReportsInvalidJson()
    {
        Equal(1, RecordEntries.Validate("{").Count);
        Equal(1, RecordEntries.Validate("[]").Count);
        var duplicate = Entry("pack", 1, value: "1").Replace("\"status\"", "\"project\":\"main\",\"status\"", StringComparison.Ordinal);
        Equal(1, RecordEntries.Validate(duplicate).Count);
        var unknown = Entry("pack", 1, value: "1").Replace("\"status\"", "\"extra\":1,\"status\"", StringComparison.Ordinal);
        Equal(true, RecordEntries.Validate(unknown).Any(problem => problem.Contains("'extra'", StringComparison.Ordinal)));
    }

    private static void CountsLengthsInCodePoints()
    {
        // 200 emoji are 400 UTF-16 units but 200 code points, which the schema allows.
        var name = string.Concat(Enumerable.Repeat("😀", 200));
        Equal(0, RecordEntries.Validate(Entry(name, 1, value: "1")).Count);
        Equal(1, RecordEntries.Validate(Entry(name + "x", 1, value: "1")).Count);
    }

    // The schema's patterns are ECMA-262, where $ does not match before a final line break and \s
    // includes U+FEFF but not U+0085. CI's schema validator uses Python's engine, which differs on
    // both, so these cases are checked here rather than as files in examples/record/invalid/.
    private static void MatchesTheSchemasPatternEngine()
    {
        Equal(true, RecordEntries.Validate(Entry("build\n", 1, value: "1")).Count > 0);
        Equal(true, RecordEntries.Validate(Entry("pack", 1, value: "12\n")).Count > 0);
        Equal(true, RecordEntries.Validate(Entry("pack", 1, value: "1", key: "﻿Suite.Case")).Count > 0);
        Equal(0, RecordEntries.Validate(Entry("pack", 1, value: "1", key: "Suite.Case\u0085")).Count);
        Equal(false, RecordEntries.IsSafeSourcePath("a/b\n"));

        var whole = Entry("pack", 1, value: "1").Replace("\"attempt\":1", "\"attempt\":1.0", StringComparison.Ordinal);
        Equal(0, RecordEntries.Validate(whole).Count);
        Equal(1, RecordEntries.Parse(whole).Attempt);
        var huge = Entry("pack", 1, value: "1").Replace("\"attempt\":1", "\"attempt\":2147483648", StringComparison.Ordinal);
        Equal(1, RecordEntries.Validate(huge).Count);
        var fraction = Entry("pack", 1, value: "1").Replace("\"attempt\":1", "\"attempt\":1.5", StringComparison.Ordinal);
        Equal(1, RecordEntries.Validate(fraction).Count);

        var deep = string.Concat(Enumerable.Repeat("[", 200)) + string.Concat(Enumerable.Repeat("]", 200));
        Equal(0, RecordEntries.Validate(DataEntry("test", deep)).Count);
        var tooDeep = string.Concat(Enumerable.Repeat("[", 300)) + string.Concat(Enumerable.Repeat("]", 300));
        Equal(1, RecordEntries.Validate(DataEntry("test", tooDeep)).Count);

        var loneSurrogate = Entry("lint", 1, status: "skipped").Replace("Not run on this platform.", "\\ud800", StringComparison.Ordinal);
        Equal(1, RecordEntries.Validate(loneSurrogate).Count);
    }

    private static void ReadsPastBadBytes()
    {
        var path = TempFile();
        try
        {
            File.WriteAllBytes(path, [0xFF, (byte)'\n']);
            EntriesFile.Append(path, Entry("pack", 1, value: "1"));
            File.AppendAllText(path, Entry("lint", 1, status: "skipped").Replace("Not run on this platform.", "\\udc00", StringComparison.Ordinal) + "\n");
            EntriesFile.Append(path, Entry("pack", 1, value: "1", dataset: "warnings"));
            var content = EntriesFile.Read(path);
            Equal(2, content.Entries.Count);
            Equal(2, content.Problems.Count);
            Equal(true, content.Problems[0].StartsWith("Line 1:", StringComparison.Ordinal));
            Equal(true, content.Problems[1].StartsWith("Line 3:", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void NamesEntriesFiles()
    {
        var name = EntriesFile.FileName("test (linux)", 2);
        Equal(name, EntriesFile.FileName("test (linux)", 2));
        Equal(true, name.StartsWith("test-linux.2.", StringComparison.Ordinal) && name.EndsWith(".jsonl", StringComparison.Ordinal));
        if (EntriesFile.FileName("a/b", 1) == EntriesFile.FileName("a:b", 1))
        {
            throw new InvalidOperationException("Names that sanitise alike must not collide.");
        }

        Equal(true, EntriesFile.FileName("../..", 1).StartsWith("job.1.", StringComparison.Ordinal));
        Equal(true, EntriesFile.FileName(new string('x', 500), 1).Length < 100);
        Throws<ArgumentOutOfRangeException>(() => EntriesFile.FileName("pack", 0));
    }

    private static void AppendsAndReadsEntriesFiles()
    {
        var path = TempFile();
        try
        {
            EntriesFile.Append(path, Entry("pack", 1, value: "1"));
            File.AppendAllText(path, "not json\n");
            EntriesFile.Append(path, Entry("pack", 1, value: "2", replaces: true));
            var content = EntriesFile.Read(path);
            Equal(2, content.Entries.Count);
            Equal("2", content.Entries[1].Value!.Value);
            Equal(1, content.Problems.Count);
            Equal(true, content.Problems[0].StartsWith("Line 2:", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void RefusesToAppendAnInvalidEntry()
    {
        var path = TempFile();
        try
        {
            EntriesFile.Append(path, Entry("pack", 1, value: "1"));
            var before = File.ReadAllText(path);
            Throws<JsonException>(() => EntriesFile.Append(path, Entry("pack", 1, value: "1.50")));
            Equal(before, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void ReportsAnEntryForAnotherCommit()
    {
        var result = Merge(
            Entry("pack", 1, value: "1"),
            Entry("test", 1, value: "1", dataset: "warnings", commit: OtherCommit));
        Equal(1, result.Entries.Count);
        Equal("packageSize", result.Entries[0].Dataset);
        Equal(MergeFindingKind.OtherCommit, Single(result.Findings).Kind);
    }

    private static void KeepsOnlyTheLatestAttempt()
    {
        var result = Merge(
            Entry("pack", 1, value: "1"),
            Entry("pack", 1, value: "5", dataset: "warnings"),
            Entry("pack", 10, value: "2"),
            Entry("pack", 2, value: "3"));
        Equal(1, result.Entries.Count);
        Equal("2", result.Entries[0].Value!.Value);
        Equal("pack attempt 1,pack attempt 2", string.Join(",", result.SupersededAttempts));
        Equal(0, result.Findings.Count);
    }

    private static void AppliesReplaceWithinAJob()
    {
        var replaced = Merge(Entry("pack", 1, value: "1"), Entry("pack", 1, value: "2", replaces: true));
        Equal("2", Single(replaced.Entries).Value!.Value);
        Equal(0, replaced.Findings.Count);

        var unreplaced = Merge(Entry("pack", 1, value: "1"), Entry("pack", 1, value: "2"), Entry("other", 1, value: "1"));
        Equal(MergeFindingKind.Conflict, Single(unreplaced.Findings).Kind);
        Equal(0, unreplaced.Entries.Count);
    }

    private static void KeepsIdenticalEntriesOnceFromTheFirstJob()
    {
        var result = Merge(
            Entry("pack-b", 1, value: "1"),
            Entry("pack-a", 1, value: "1"),
            Entry("pack-a", 1, value: "1"),
            Entry("lint-b", 1, dataset: "warnings", status: "skipped"),
            Entry("lint-a", 1, dataset: "warnings", status: "skipped"));
        Equal(2, result.Entries.Count);
        Equal(0, result.Findings.Count);
        Equal("pack-a", result.Entries.Single(entry => entry.Dataset == "packageSize").JobName);
        Equal("lint-a", result.Entries.Single(entry => entry.Dataset == "warnings").JobName);

        var data = Merge(
            DataEntry("test-b", "{\"b\":1,\"a\":[1,2]}"),
            DataEntry("test-a", "{\"a\":[1,2],\"b\":1}"));
        Equal("test-a", Single(data.Entries).JobName);
        Equal(0, data.Findings.Count);
    }

    private static void ReportsDifferentValuesWithoutChoosing()
    {
        var result = Merge(Entry("pack-a", 1, value: "1"), Entry("pack-b", 1, value: "2"), Entry("pack-c", 1, value: "1"));
        Equal(0, result.Entries.Count);
        var finding = Single(result.Findings);
        Equal(MergeFindingKind.Conflict, finding.Kind);
        Equal(true, finding.Message.Contains("'pack-a' and 'pack-b'", StringComparison.Ordinal));

        var dimensions = Merge(
            Entry("test-linux", 1, value: "1", dimension: "linux"),
            Entry("test-windows", 1, value: "2", dimension: "windows"));
        Equal(2, dimensions.Entries.Count);
        Equal(0, dimensions.Findings.Count);
    }

    private static void ReportsRecordedAgainstSkipped()
    {
        var result = Merge(Entry("pack", 1, value: "1"), Entry("other", 1, status: "failed"));
        Equal(0, result.Entries.Count);
        Equal(MergeFindingKind.StatusConflict, Single(result.Findings).Kind);
    }

    private static void ReportsValueAgainstData()
    {
        var result = Merge(
            Entry("pack", 1, value: "1", dataset: "tests", key: "Suite.Case"),
            DataEntry("test", "{\"a\":1}"));
        Equal(0, result.Entries.Count);
        Equal(MergeFindingKind.ValueAndData, Single(result.Findings).Kind);
    }

    private static void ReportsValueAgainstDataPastAConflict()
    {
        var keyless = Merge(Entry("pack", 1, value: "1", dataset: "tests"), DataEntry("test", "{\"a\":1}"));
        Equal(0, keyless.Entries.Count);
        Equal(MergeFindingKind.ValueAndData, Single(keyless.Findings).Kind);

        var conflicted = Merge(
            Entry("pack-a", 1, value: "1", dataset: "tests", key: "Suite.Case"),
            Entry("pack-b", 1, value: "2", dataset: "tests", key: "Suite.Case"),
            DataEntry("test-c", "{\"a\":1}"));
        Equal(0, conflicted.Entries.Count);
        Equal(MergeFindingKind.ValueAndData, Single(conflicted.Findings).Kind);
    }

    private static void ReportsWhatWasNotCollected()
    {
        var linux = new Dictionary<string, string>(StringComparer.Ordinal) { ["os"] = "linux" };
        var windows = new Dictionary<string, string>(StringComparer.Ordinal) { ["os"] = "windows" };
        var none = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = EntryMerge.Merge(
            Commit,
            [
                RecordEntries.Parse(Entry("test-linux", 1, value: "1", dimension: "linux")),
                RecordEntries.Parse(Entry("lint", 1, dataset: "warnings", status: "skipped")),
                RecordEntries.Parse(Rejected("size", "coverage")),
            ],
            [
                new ExpectedEntry("main", "packageSize", linux),
                new ExpectedEntry("main", "packageSize", windows),
                new ExpectedEntry("main", "warnings", none),
                new ExpectedEntry("main", "coverage", none),
            ]);
        var missing = result.Findings.Where(finding => finding.Kind == MergeFindingKind.NotCollected).Select(finding => finding.Message).ToList();
        Equal(2, missing.Count);
        Equal("main/coverage was not collected.", missing[0]);
        Equal("main/packageSize (os=windows) was not collected.", missing[1]);
    }

    private static void KeepsRejectedEntriesOutOfConflicts()
    {
        var result = Merge(
            Entry("pack", 1, value: "1"),
            Rejected("pack-b", "packageSize"),
            Rejected("pack-b", "packageSize"));
        Equal(2, result.Entries.Count);
        Equal(EntryStatus.Recorded, result.Entries[0].Status);
        Equal(EntryStatus.Rejected, result.Entries[1].Status);
        Equal(0, result.Findings.Count);
    }

    private static MergeResult Merge(params string[] entries) =>
        EntryMerge.Merge(Commit, entries.Select(RecordEntries.Parse), []);

    private static string Entry(
        string job,
        int attempt,
        string? value = null,
        string? unit = null,
        string dataset = "packageSize",
        string status = "recorded",
        string? key = null,
        string? dimension = null,
        bool replaces = false,
        string commit = Commit)
    {
        var fields = new Dictionary<string, object>
        {
            ["schema"] = RecordEntries.SchemaId,
            ["project"] = "main",
            ["dataset"] = dataset,
            ["commit"] = commit,
            ["job"] = new Dictionary<string, object> { ["name"] = job, ["attempt"] = attempt },
        };
        if (dimension is not null)
        {
            fields["dimensions"] = new Dictionary<string, string> { ["os"] = dimension };
        }

        if (key is not null)
        {
            fields["key"] = key;
        }

        fields["status"] = status;
        if (replaces)
        {
            fields["replaces"] = true;
        }

        if (value is not null)
        {
            var scalar = new Dictionary<string, string> { ["value"] = value };
            if (unit is not null)
            {
                scalar["unit"] = unit;
            }

            fields["value"] = scalar;
        }

        if (status is "skipped" or "failed")
        {
            fields["reason"] = "Not run on this platform.";
        }

        return JsonSerializer.Serialize(fields);
    }

    private static string DataEntry(string job, string data) =>
        $$"""{"schema":"{{RecordEntries.SchemaId}}","project":"main","dataset":"tests","commit":"{{Commit}}","job":{"name":"{{job}}","attempt":1},"status":"recorded","sources":[{"path":"artifacts/unit.junit.xml","format":"junit","sha256":"{{new string('a', 64)}}"}],"data":{{data}}}""";

    private static string Rejected(string job, string dataset) =>
        $$"""{"schema":"{{RecordEntries.SchemaId}}","project":"main","dataset":"{{dataset}}","commit":"{{Commit}}","job":{"name":"{{job}}","attempt":1},"status":"rejected","input":{"value":"12,5"},"problems":["'12,5' is not a number."]}""";

    private static string TempFile()
    {
        var directory = Path.Combine(FindRepositoryRoot(), "temp");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"entries-{Guid.NewGuid():N}.jsonl");
    }

    private static T Single<T>(IReadOnlyList<T> items)
    {
        if (items.Count != 1)
        {
            throw new InvalidOperationException($"Expected one item, got {items.Count}: {string.Join("; ", items)}");
        }

        return items[0];
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Diffra.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root.");
    }
}
