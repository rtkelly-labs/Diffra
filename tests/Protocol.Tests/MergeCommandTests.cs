using System.Text.Json;
namespace Diffra.Protocol.Tests;

/// <summary>
/// Runs <c>diffra merge</c> as a process over entries files written by <c>diffra record</c>: what
/// the merge finds is reported with exit code 0, the merged entries are valid, and entries files
/// are read as untrusted data.
/// </summary>
public static class MergeCommandTests
{
    private const string Commit = RecordCommandTests.Commit;
    private const string Other = "--other-commit";

    // A smaller root file than the record tests use, so that every required dataset is listed.
    private const string RootFile = """
        schema: https://diffra.net/schemas/root-file-v1.schema.json
        dimensions:
          os: { values: [linux, windows] }
          runner: { observed: true }
        datasets:
          packageSize:
            type: bytes
            stratify: [os, runner]
            from: { payload: "artifacts/size.json" }
          warnings:
            type: count
            from: { payload: "artifacts/metrics.json" }
          ratio:
            type: decimal
            required: false
            from: { payload: "artifacts/metrics.json" }
        projects:
          main:
            datasets: { packageSize: null, warnings: null, ratio: null }
        """;

    public static void RunAll()
    {
        MergesTheJobsAndReportsWhatItFinds();
        ExitsTwoWithoutAUsableContext();
        ReadsEntriesFilesAsUntrustedData();
        WritesUnderTheRunFolderByDefault();
        ReportsEveryKindOfFinding();
        ReadsEachFolderAndEachContentOnce();
        LimitsWhatItReports();
        EscapesControlCharactersInTheReport();
        MergesManyJobsQuickly();
        SurvivesHostileEntriesFiles();
        KeepsOneCopyOfAJobAttempt();
        RefusesOutputsThatReplaceInputs();
        Console.WriteLine("PASS diffra merge");
    }

    private static void MergesTheJobsAndReportsWhatItFinds()
    {
        using var run = new Folder();
        run.Record("linux", 1, "--name", "packageSize", "--value", "10", "--dimension", "os=linux", "--dimension", "runner=a");
        run.Record("linux", 1, "--name", "warnings", "--value", "3");
        run.Record("lint", 1, "--name", "warnings", "--value", "4");
        run.Record("lint", 1, "--name", "ratio", "--value", "0.5");
        run.Record("pack", 1, "--name", "ratio", "--value", "0.50");
        run.Record("pack", 1, "--name", "ratio", "--value", "nope");
        run.Record("pack", 2, "--name", "ratio", "--value", "0.5");

        var result = run.Merge("--json");
        Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;
        Equal(Commit, root.GetProperty("commit").GetString());
        Equal(4, root.GetProperty("entriesFiles").GetInt32());
        Equal(
            "conflict: main/warnings has 2 different values across jobs 'lint' and 'linux'.|not-collected: main/packageSize (os=windows) was not collected.",
            string.Join("|", root.GetProperty("findings").EnumerateArray().Select(finding => finding.GetProperty("kind").GetString() + ": " + finding.GetProperty("message").GetString())));
        Equal(1, root.GetProperty("supersededAttempts").GetArrayLength());
        Equal(0, root.GetProperty("unreadable").GetArrayLength());

        // packageSize carries the observed runner as well as os, and still matches the plan for
        // linux, which names os only.

        // The merged file holds valid entries: packageSize for linux, ratio once, and the rejected
        // entry of the attempt that was superseded is gone with it.
        var lines = File.ReadAllLines(run.Output);
        Equal(true, lines.All(line => RecordEntries.Validate(line).Count == 0));
        var merged = lines.Select(RecordEntries.Parse).ToList();
        Equal("packageSize 10,ratio 0.5", string.Join(",", merged.Select(entry => $"{entry.Dataset} {entry.Value?.Value}")));
        Equal(lines.Length, root.GetProperty("entries").GetInt32());

        // The same inputs give the same output.
        var first = File.ReadAllText(run.Output);
        Equal(0, run.Merge().ExitCode);
        Equal(first, File.ReadAllText(run.Output));
    }

    private static void ExitsTwoWithoutAUsableContext()
    {
        using var run = new Folder();
        Equal(2, run.Merge(withCommit: false).ExitCode);
        Equal(2, run.Merge(withCommit: false, "--commit", "abc").ExitCode);
        Equal(2, RecordCommandTests.RunCli(run.Directory, new Dictionary<string, string>(), "merge", "--root-file", Path.Combine(run.Directory, "missing.yml"), "--commit", Commit, "--entries", run.Entries, "-o", run.Output).ExitCode);
        Equal(2, RecordCommandTests.RunCli(run.Directory, new Dictionary<string, string>(), "merge", "--commit", Commit, "--entries", run.Entries, "-o", run.Output).ExitCode);
        Equal(2, run.Merge("--root-file", run.RootFilePath).ExitCode);
        Equal(2, run.Merge("--entries", Path.Combine(run.Directory, "nowhere")).ExitCode);
        Equal(2, run.Merge("--colour", "red").ExitCode);
        Equal(2, run.Merge("stray").ExitCode);
        Equal(2, run.Merge("-o").ExitCode);
        Equal(2, run.Merge("--entries", string.Empty).ExitCode);
        Equal(2, run.Merge(withCommit: false, "--commit", string.Empty).ExitCode);
        Equal(2, run.Merge("--o", run.Output).ExitCode);
        Equal(false, File.Exists(run.Output));

        // The output may not be written where a later merge would read it as an entries file, nor
        // be a folder.
        Equal(2, run.Raw("--entries", run.Entries, "-o", Path.Combine(run.Entries, "merged.jsonl")).ExitCode);
        Equal(2, run.Raw("--entries", run.Entries + Path.DirectorySeparatorChar, "-o", Path.Combine(run.Entries, "merged.jsonl")).ExitCode);
        Equal(2, run.Raw("--entries", run.Entries, "-o", run.Directory).ExitCode);
        Equal(2, run.Raw("--entries", run.Entries, "-o", run.Directory + Path.DirectorySeparatorChar).ExitCode);
        Equal(0, Directory.GetFiles(run.Entries).Length);
    }

    private static void ReportsEveryKindOfFinding()
    {
        using var run = new Folder();
        run.Record("linux", 1, "--name", "packageSize", "--value", "10", "--dimension", "os=linux", "--dimension", "runner=a");
        run.Record("windows", 1, "--name", "packageSize", "--skipped", "--reason", "not built", "--dimension", "os=windows");
        run.Record("lint", 1, "--name", "warnings", "--value", "3");
        run.Record("pack", 1, "--name", "warnings", "--failed", "--reason", "broke");
        run.Record("old", 1, Other, "--name", "ratio", "--value", "1");

        var result = run.Merge("--json");
        Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var kinds = document.RootElement.GetProperty("findings").EnumerateArray()
            .Select(finding => finding.GetProperty("kind").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Equal(true, kinds.Contains("other-commit"));
        Equal(true, kinds.Contains("status-conflict"));
    }

    private static void ReadsEachFolderAndEachContentOnce()
    {
        using var run = new Folder();
        run.Record("linux", 1, "--name", "warnings", "--value", "3");
        var file = Directory.GetFiles(run.Entries).Single();
        File.Copy(file, Path.Combine(run.Entries, "zz-copy.jsonl"));

        var result = run.Merge("--json", "--entries", run.Entries, "--entries", run.Entries + Path.DirectorySeparatorChar);
        Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Equal(1, document.RootElement.GetProperty("entriesFiles").GetInt32());
        Equal(0, document.RootElement.GetProperty("findings").EnumerateArray().Count(finding => finding.GetProperty("kind").GetString() == "conflict"));
        var unreadable = document.RootElement.GetProperty("unreadable").EnumerateArray().Single();
        Equal(true, unreadable.GetProperty("message").GetString()!.StartsWith("The file has the same content as ", StringComparison.Ordinal));
    }

    private static void LimitsWhatItReports()
    {
        using var run = new Folder();
        File.WriteAllText(Path.Combine(run.Entries, "bad.jsonl"), string.Concat(Enumerable.Repeat("not json\n", 25)));
        var result = run.Merge("--json");
        Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var messages = document.RootElement.GetProperty("unreadable").EnumerateArray().Select(problem => problem.GetProperty("message").GetString()).ToList();
        Equal(21, messages.Count);
        Equal("5 more lines were not valid entries.", messages[^1]);
    }

    private static void EscapesControlCharactersInTheReport()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var run = new Folder();
        File.WriteAllText(Path.Combine(run.Entries, "a\nconflict: forged\u2028.jsonl"), "not json\n");
        var result = run.Merge();
        Equal(0, result.ExitCode);
        Equal(false, result.Output.Split('\n').Any(line => line.StartsWith("conflict: forged", StringComparison.Ordinal)));
        Equal(false, result.Output.Contains('\u2028', StringComparison.Ordinal));
        Equal(true, result.Output.Contains("a\\u000aconflict: forged\\u2028.jsonl", StringComparison.Ordinal));
    }

    // Many jobs that agree on one value, and one job with many rejected entries, merge in a few
    // seconds rather than in time that grows with the square of the entries.
    private static void MergesManyJobsQuickly()
    {
        using var run = new Folder();
        run.Record("quick", 1, "--name", "warnings", "--value", "3");
        var line = File.ReadAllLines(Directory.GetFiles(run.Entries).Single()).Single();
        File.WriteAllLines(
            Path.Combine(run.Entries, "many.jsonl"),
            Enumerable.Range(0, 4000).Select(index => line.Replace("\"name\":\"quick\"", $"\"name\":\"quick{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"", StringComparison.Ordinal)));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = run.Merge("--json");
        Equal(0, result.ExitCode);
        Equal(true, watch.Elapsed < TimeSpan.FromSeconds(60));
        using var document = JsonDocument.Parse(result.Output);
        Equal(1, document.RootElement.GetProperty("entries").GetInt32());
        Equal(0, document.RootElement.GetProperty("unreadable").GetArrayLength());

        // Data that differs only in numbers: every job disagrees, and the merge is still quick.
        using var numbers = new Folder();
        File.WriteAllLines(
            Path.Combine(numbers.Entries, "data.jsonl"),
            Enumerable.Range(0, 4000).Select(index => DataEntry($"job{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}", 1, index.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        watch.Restart();
        var differing = numbers.Merge("--json");
        Equal(0, differing.ExitCode);
        Equal(true, watch.Elapsed < TimeSpan.FromSeconds(60));
    }

    private static void SurvivesHostileEntriesFiles()
    {
        using var run = new Folder();

        // A lone surrogate in a data value is a line problem, not a crash.
        File.WriteAllText(Path.Combine(run.Entries, "a.jsonl"), DataEntry("a", 1, "\"\\ud800\"") + "\n");

        // A file of invalid lines is read only up to a limit.
        File.WriteAllText(Path.Combine(run.Entries, "b.jsonl"), string.Concat(Enumerable.Repeat("x\n", 5000)));

        // More findings than are listed.
        var other = new string('b', 40);
        File.WriteAllLines(
            Path.Combine(run.Entries, "c.jsonl"),
            Enumerable.Range(0, 1500).Select(index => DataEntry($"c{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}", 1, "1").Replace(Commit, other, StringComparison.Ordinal)));

        var result = run.Merge("--json");
        Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;
        var messages = root.GetProperty("unreadable").EnumerateArray().Select(problem => problem.GetProperty("message").GetString()!).ToList();
        Equal(true, messages.Exists(message => message.Contains("not valid Unicode", StringComparison.Ordinal)));
        Equal(true, messages.Contains("Reading stopped after 100 lines that were not valid entries."));
        Equal(true, messages.Contains("80 more lines were not valid entries."));
        Equal(1000, root.GetProperty("findings").GetArrayLength());
        Equal(true, root.GetProperty("findingsNotListed").GetInt32() >= 500);

        var plain = run.Merge();
        Equal(true, plain.Output.Contains(" more findings are not listed.", StringComparison.Ordinal));
    }

    // Two files for one job attempt are copies, one perhaps written further: the longer one is kept,
    // with no conflict between them.
    private static void KeepsOneCopyOfAJobAttempt()
    {
        using var run = new Folder();
        run.Record("r", 1, "--name", "warnings", "--value", "1");
        run.Record("r", 1, "--name", "warnings", "--value", "2", "--replace");
        var file = Directory.GetFiles(run.Entries).Single();
        File.Copy(file, Path.Combine(run.Entries, "a-copy.jsonl"));
        File.AppendAllText(Path.Combine(run.Entries, "a-copy.jsonl"), "\n");
        run.Record("r", 1, "--name", "warnings", "--value", "3", "--replace");

        var result = run.Merge("--json");
        Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Equal(0, document.RootElement.GetProperty("findings").EnumerateArray().Count(finding => finding.GetProperty("kind").GetString() == "conflict"));
        Equal("3", RecordEntries.Parse(File.ReadAllLines(run.Output).Single(line => line.Contains("\"warnings\"", StringComparison.Ordinal))).Value!.Value);
        Equal(true, document.RootElement.GetProperty("unreadable").EnumerateArray()
            .Any(problem => Path.GetFileName(problem.GetProperty("file").GetString()) == "a-copy.jsonl"));
    }

    private static void RefusesOutputsThatReplaceInputs()
    {
        using var run = new Folder();
        Equal(2, run.Raw("--entries", run.Entries, "-o", run.RootFilePath).ExitCode);
        Equal(RootFile, File.ReadAllText(run.RootFilePath));

        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(run.Directory, "link");
            Directory.CreateSymbolicLink(link, Path.Combine(run.Directory, "run"));
            Equal(2, run.Raw("--entries", run.Entries, "-o", Path.Combine(link, "entries", "merged.jsonl")).ExitCode);
            Equal(0, Directory.GetFiles(run.Entries).Length);
        }

        // A bidirectional control in an argument is escaped in the error message.
        var bidi = run.Raw("--colour\u202e");
        Equal(2, bidi.ExitCode);
        Equal(false, bidi.Error.Contains('\u202e', StringComparison.Ordinal));
    }

    // A recorded entry read from files, with the given data value for key x.
    private static string DataEntry(string job, int attempt, string value) =>
        "{\"schema\":\"https://diffra.net/schemas/record-entry-v1.schema.json\",\"project\":\"main\",\"dataset\":\"warnings\",\"commit\":\"" + Commit
        + "\",\"job\":{\"name\":\"" + job + "\",\"attempt\":" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "},\"status\":\"recorded\",\"sources\":[{\"path\":\"a.json\",\"format\":\"payload\",\"sha256\":\"2d711642b726b04401627ca9fbac32f5c8530fb1903cc4db02258717921a4881\"}],\"data\":{\"x\":" + value + "}}";

    private static void ReadsEntriesFilesAsUntrustedData()
    {
        using var run = new Folder();
        run.Record("linux", 1, "--name", "warnings", "--value", "3");
        var file = Directory.GetFiles(run.Entries).Single();
        File.AppendAllText(file, "not json\n{\"schema\":1}\n");
        File.WriteAllText(Path.Combine(run.Entries, "notes.txt"), "ignored");
        Directory.CreateDirectory(Path.Combine(run.Entries, "nested.jsonl"));
        var elsewhere = Path.Combine(run.Directory, "elsewhere.jsonl");
        File.WriteAllText(elsewhere, string.Empty);
        File.CreateSymbolicLink(Path.Combine(run.Entries, "link.jsonl"), elsewhere);

        var result = run.Merge("--json");
        Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var unreadable = document.RootElement.GetProperty("unreadable").EnumerateArray()
            .Select(problem => Path.GetFileName(problem.GetProperty("file").GetString()) + ": " + problem.GetProperty("message").GetString())
            .ToList();
        Equal(3, unreadable.Count);
        Equal("link.jsonl: The file is a link and was not read.", unreadable[0]);
        Equal(true, unreadable[1].Contains(": Line 2:", StringComparison.Ordinal));
        Equal(true, unreadable[2].Contains(": Line 3:", StringComparison.Ordinal));
        Equal(1, document.RootElement.GetProperty("entriesFiles").GetInt32());
        Equal(1, File.ReadAllLines(run.Output).Length);
    }

    private static void WritesUnderTheRunFolderByDefault()
    {
        using var run = new Folder();
        run.Record("linux", 1, "--name", "warnings", "--value", "3");
        var environment = new Dictionary<string, string>
        {
            ["DIFFRA_COMMIT"] = Commit,
            ["DIFFRA_ROOT_FILE"] = run.RootFilePath,
            ["DIFFRA_RUN_DIR"] = Path.Combine(run.Directory, "run"),
        };
        var result = RecordCommandTests.RunCli(run.Directory, environment, "merge");
        Equal(0, result.ExitCode);
        Equal(true, result.Output.StartsWith("Merged 1 entry from 1 entries file for commit ", StringComparison.Ordinal));
        Equal(1, File.ReadAllLines(Path.Combine(run.Directory, "run", "merged.jsonl")).Length);

        // No job recorded anything: every required dataset is reported, and the merge still succeeds.
        using var empty = new Folder();
        var nothing = RecordCommandTests.RunCli(empty.Directory, new Dictionary<string, string>
        {
            ["DIFFRA_COMMIT"] = Commit,
            ["DIFFRA_ROOT_FILE"] = empty.RootFilePath,
            ["DIFFRA_RUN_DIR"] = Path.Combine(empty.Directory, "run"),
        }, "merge");
        Equal(0, nothing.ExitCode);
        Equal(3, nothing.Output.Split('\n').Count(line => line.StartsWith("not-collected: ", StringComparison.Ordinal)));
        Equal(0, File.ReadAllLines(Path.Combine(empty.Directory, "run", "merged.jsonl")).Length);
    }

    private sealed class Folder : IDisposable
    {
        public Folder()
        {
            Directory = Path.Combine(FindRepositoryRoot(), "temp", $"merge-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
            RootFilePath = Path.Combine(Directory, "diffra.yml");
            File.WriteAllText(RootFilePath, RootFile);
            System.IO.Directory.CreateDirectory(Entries);
        }

        public string Directory { get; }

        public string RootFilePath { get; }

        public string Entries => Path.Combine(Directory, "run", "entries");

        public string Output => Path.Combine(Directory, "out", "merged.jsonl");

        public void Record(string job, int attempt, params string[] arguments)
        {
            var commit = arguments is [Other, ..] ? new string('b', 40) : Commit;
            arguments = arguments is [Other, .. var rest] ? rest : arguments;
            var result = RecordCommandTests.RunCli(
                Directory,
                new Dictionary<string, string>(),
                ["record", .. arguments, "--project", "main", "--commit", commit, "--job", job, "--attempt", attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--root-file", RootFilePath, "--run-dir", Path.Combine(Directory, "run")]);
            Equal(0, result.ExitCode);
        }

        public (int ExitCode, string Output, string Error) Merge(params string[] arguments) => Merge(withCommit: true, arguments);

        public (int ExitCode, string Output, string Error) Merge(bool withCommit, params string[] arguments)
        {
            var all = new List<string> { "merge", "--root-file", RootFilePath, "-o", Output };
            if (withCommit)
            {
                all.AddRange(["--commit", Commit]);
            }

            if (!arguments.Contains("--entries"))
            {
                all.AddRange(["--entries", Entries]);
            }

            all.AddRange(arguments);
            return RecordCommandTests.RunCli(Directory, new Dictionary<string, string>(), [.. all]);
        }

        // merge with only the root file and commit added.
        public (int ExitCode, string Output, string Error) Raw(params string[] arguments) =>
            RecordCommandTests.RunCli(Directory, new Dictionary<string, string>(), ["merge", "--root-file", RootFilePath, "--commit", Commit, .. arguments]);

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary folder is harmless.
            }
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Diffra.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
