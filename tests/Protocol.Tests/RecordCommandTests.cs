using System.Diagnostics;
using System.Text.Json;

namespace Diffra.Protocol.Tests;

/// <summary>
/// Runs <c>diffra record</c> as a process: values are checked against the root file, every problem
/// after the context is saved as a valid rejected entry with exit code 0, and the context, the run
/// folder and the dimensions are read from options, then the environment.
/// </summary>
public static class RecordCommandTests
{
    internal const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private const string RootFile = """
        schema: https://diffra.net/schemas/root-file-v1.schema.json
        dimensions:
          os: { values: [linux, windows] }
          runner: { observed: true }
        datasets:
          packageSize:
            type: bytes
            stratify: [os]
            from: { payload: "artifacts/size.json" }
          suiteTime:
            type: duration
            collection: { kind: map, keys: { normalization: none } }
            from: { payload: "artifacts/suites.json" }
          warnings:
            type: count
            unit: warnings
            from: { payload: "artifacts/metrics.json" }
          ratio:
            type: decimal
            from: { payload: "artifacts/metrics.json" }
          mode:
            type: enum
            values: [fast, slow]
            from: { payload: "artifacts/metrics.json" }
          byRunner:
            type: integer
            stratify: [runner]
            from: { payload: "artifacts/metrics.json" }
          unselected:
            type: integer
            from: { payload: "artifacts/metrics.json" }
          packages:
            type: diffra/size@1
            from: { files: "artifacts/*.nupkg" }
        projects:
          main:
            datasets: { packageSize: null, suiteTime: null, warnings: null, ratio: null, mode: null, byRunner: null, packages: null }
        """;

    public static void RunAll()
    {
        RecordsAValueInCanonicalForm();
        RecordsSkippedAndFailed();
        ExitsTwoWithoutAUsableContext();
        RejectsWhatTheRootFileDoesNotAllow();
        AlwaysSavesAValidEntry();
        RefusesADifferentValueUnlessReplaced();
        ReadsTheEnvironment();
        WritesUnderTheGitDirectory();
        AnswersInJson();
        ReadsFilesWithTheConverter();
        RejectsFilesItCannotRead();
        Console.WriteLine("PASS diffra record");
    }

    private static void RecordsAValueInCanonicalForm()
    {
        foreach (var (given, canonical) in new[]
        {
            ("12", "12"), ("+012.50", "12.5"), ("1.25e1", "12.5"), ("-0", "0"), ("-0.000", "0"),
            ("1e-3", "0.001"), (".5", "0.5"), ("5.", "5"), ("-7.10", "-7.1"), ("1E2", "100"),
        })
        {
            using var run = new Run();
            var result = run.Record("--name", "ratio", "--value", given);
            Equal(0, result.ExitCode);
            var entry = Single(run.Entries());
            Equal(EntryStatus.Recorded, entry.Status);
            Equal(canonical, entry.Value!.Value);
            Equal(true, entry.Value.Unit is null);
        }

        using var keyed = new Run();
        Equal(0, keyed.Record("--name", "suiteTime", "--value", "3.5", "--key", "Suite A", "--unit", "s").ExitCode);
        var suite = Single(keyed.Entries());
        Equal("Suite A", suite.Key);
        Equal("s", suite.Value!.Unit);

        using var stratified = new Run();
        Equal(0, stratified.Record("--name", "packageSize", "--value", "1024", "--unit", "B", "--dimension", "os=linux").ExitCode);
        var size = Single(stratified.Entries());
        Equal("linux", size.Dimensions["os"]);
        Equal(null, size.Value!.Unit); // B is the fixed unit of bytes, so it is not written.
        Equal(true, File.Exists(Path.Combine(stratified.Directory, "run", "entries", EntriesFile.FileName("pack", 1))));
    }

    private static void RecordsSkippedAndFailed()
    {
        using var run = new Run();
        Equal(0, run.Record("--name", "warnings", "--skipped", "--reason", "Not built on this runner.").ExitCode);
        Equal(0, run.Record("--name", "ratio", "--failed", "--reason", "The collector crashed.").ExitCode);
        var entries = run.Entries();
        Equal(EntryStatus.Skipped, entries[0].Status);
        Equal(EntryStatus.Failed, entries[1].Status);
    }

    private static void ExitsTwoWithoutAUsableContext()
    {
        string[][] cases =
        [
            ["--value", "1"],
            ["--name", "", "--value", "1"],
            ["--name", new string('a', 201), "--value", "1"],
            ["--name", "ratio", "--name", "warnings", "--value", "1"],
            ["--name", "ratio", "--value", "1", "--commit", "abc123"],
            ["--name", "ratio", "--value", "1", "--commit", Commit.ToUpperInvariant()],
            ["--name", "ratio", "--value", "1", "--job", " pack"],
            ["--name", "ratio", "--value", "1", "--job", "pa\tck"],
            ["--name", "ratio", "--value", "1", "--job", "\ufeffpack"],
            ["--name", "ratio", "--value", "1", "--run-dir", "a", "--run-dir", "b"],
            ["--name", "ratio", "--value", "1", "--attempt", "0"],
            ["--name", "ratio", "--value", "1", "--attempt", "1.5"],
            ["--name", "ratio", "--value", "1", "--attempt", "99999999999"],
        ];
        foreach (var arguments in cases)
        {
            using var run = new Run();
            var result = run.Record(arguments);
            if (result.ExitCode != 2 || result.Error.Contains("more than once", StringComparison.Ordinal) != (arguments.Count(argument => argument is "--name" or "--run-dir") > 1))
            {
                throw new InvalidOperationException($"Expected exit 2 for {string.Join(" ", arguments)}, got {result.ExitCode}: {result.Error}");
            }

            Equal(false, Directory.Exists(Path.Combine(run.Directory, "run")));
        }

        using var noProject = new Run();
        Equal(2, noProject.Record(withProject: false, "--name", "ratio", "--value", "1").ExitCode);
    }

    private static void RejectsWhatTheRootFileDoesNotAllow()
    {
        var cases = new (string[] Arguments, string Problem)[]
        {
            (["--name", "missing", "--value", "1"], "The root file declares no dataset 'missing'."),
            (["--name", "unselected", "--value", "1"], "Project 'main' does not select dataset 'unselected'."),
            (["--name", "ratio", "--value", "ten"], "'ten' is not a number."),
            (["--name", "ratio", "--value", "12,5"], "'12,5' is not a number."),
            (["--name", "ratio", "--value", "1e999"], "has an exponent outside"),
            (["--name", "ratio", "--value", "1", "--from", "x.json"], "Give at most one source"),
            (["--name", "ratio", "--from", "x.json"], "No converter is built yet for dataset type 'decimal'"),
            (["--name", "ratio"], "No converter is built yet for dataset type 'decimal'"),
            (["--name", "packages", "--value", "1"], "has type 'diffra/size@1'; --value takes a number"),
            (["--name", "packages", "--unit", "B"], "--unit applies to --value only."),
            (["--name", "mode", "--value", "1"], "--value takes a number"),
            (["--name", "warnings", "--value", "1.5"], "takes whole numbers only"),
            (["--name", "warnings", "--value", "-1"], "cannot be negative"),
            (["--name", "warnings", "--value", "1", "--unit", "errors"], "is measured in 'warnings', not 'errors'."),
            (["--name", "packageSize", "--value", "1", "--unit", "s", "--dimension", "os=linux"], "is measured in 'B', not 's'."),
            (["--name", "suiteTime", "--value", "1", "--key", "a", "--unit", "B"], "is a duration; its unit is one of ns, us, ms, s, m, h, d, not 'B'."),
            (["--name", "suiteTime", "--value", "1"], "is keyed; give the member with --key."),
            (["--name", "ratio", "--value", "1", "--key", "a"], "is not keyed"),
            (["--name", "ratio", "--value", "1", "--key", " a"], "--key must be"),
            (["--name", "packageSize", "--value", "1"], "is stratified by 'os', but no value was given"),
            (["--name", "packageSize", "--value", "1", "--dimension", "os=mac"], "Dimension 'os' has no value 'mac'"),
            (["--name", "packageSize", "--value", "1", "--dimension", "os=linux", "--dimension", "arch=x64"], "declares no dimension 'arch'"),
            (["--name", "ratio", "--value", "1", "--dimension", "runner=a"], "is not stratified by 'runner'"),
            (["--name", "ratio", "--value", "1", "--dimension", "noequals"], "is not <name>=<value>"),
            (["--name", "ratio", "--skipped"], "--skipped needs --reason"),
            (["--name", "ratio", "--value", "1", "--reason", "why"], "--reason applies to --skipped and --failed only."),
            (["--name", "ratio", "--skipped", "--reason", "x", "--replace"], "--replace applies to a value or to files read, not to --skipped or --failed."),
            (["--name", "ratio", "--value", "1", "--value", "2"], "--value was given more than once."),
            (["--name", "ratio", "--value", "1", "--colour", "red"], "Unknown option '--colour'."),
            (["--name", "ratio", "--value", "1", "stray"], "Unexpected argument 'stray'."),
            (["--name", "ratio", "--value"], "--value needs a value."),
        };
        foreach (var (arguments, problem) in cases)
        {
            using var run = new Run();
            var result = run.Record(arguments);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Exit {result.ExitCode} for {string.Join(" ", arguments)}: {result.Error}");
            }

            var problems = Problems(Single(run.Entries()));
            if (!problems.Any(text => text.Contains(problem, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"Expected a problem containing '{problem}' for {string.Join(" ", arguments)}, got: {string.Join(" | ", problems)}");
            }
        }

        using var undeclared = new Run();
        Equal(0, undeclared.Record(withProject: false, "--project", "Other App", "--name", "ratio", "--value", "1").ExitCode);
        var entry = Single(undeclared.Entries());
        Equal("Other App", entry.Project);
        Equal(true, Problems(entry).Contains("The root file declares no project 'Other App'."));

        using var noRootFile = new Run();
        Equal(0, noRootFile.Record(withRootFile: false, "--name", "ratio", "--value", "1").ExitCode);
        Equal(true, Problems(Single(noRootFile.Entries()))[0].StartsWith("No root file was given", StringComparison.Ordinal));

        using var badRootFile = new Run(rootFile: "schema: nope\n");
        Equal(0, badRootFile.Record("--name", "ratio", "--value", "1").ExitCode);
        Equal(true, Problems(Single(badRootFile.Entries()))[0].Contains("is not valid", StringComparison.Ordinal));
    }

    // Whatever is given after a usable context, the entry saved is valid and the exit code is 0.
    private static void AlwaysSavesAValidEntry()
    {
        var manyDimensions = Enumerable.Range(0, 150).SelectMany(index => new[] { "--dimension", $"d{index}=v" });
        string[][] cases =
        [
            ["--name", "ratio", "--skipped", "--reason", new string('r', 3000)],
            ["--name", "ratio", "--value", new string('9', 3000)],
            ["--name", "ratio", "--value", "1", "--dimension", "os=" + new string('x', 1500)],
            ["--name", "ratio", "--value", "1", "--dimension", new string('n', 300) + "=v"],
            ["--name", "ratio", "--value", "1", "--dimension", "os=\u0001"],
            ["--name", "byRunner", "--value", "1", "--dimension", "runner=\u0001"],
            ["--name", "byRunner", "--value", "1", "--dimension", "runner=\ufeffx"],
            ["--name", "byRunner", "--value", "1", "--dimension", "runner=x\u0085"],
            ["--name", "suiteTime", "--value", "1", "--key", "suite\ufeff"],
            ["--name", "ratio", "--value", "1", "--unit", "\ufeffms"],
            ["--name", "ratio", "--value", "1", "--unit", new string('u', 300)],
            ["--name", "ratio", "--value", "1", "--key", "a\nb"],
            ["--name", "ratio", "--value", "1", .. manyDimensions],
            ["--name", "ratio", "--value", "1", "--" + new string('z', 100), "x"],
            ["--name", "ratio", "--value", "1", "--Upper"],
            ["--name", "ratio", "--value", "1", "--root-file", "a.yml", "--root-file", "b.yml"],
            ["--name", "Not A Name", "--value", "1"],
        ];
        foreach (var arguments in cases)
        {
            using var run = new Run();
            var result = run.Record(arguments);
            Equal(0, result.ExitCode);
            var lines = File.ReadAllLines(run.EntriesFile);
            Equal(1, lines.Length);
            var problems = RecordEntries.Validate(lines[0]);
            if (problems.Count > 0)
            {
                throw new InvalidOperationException($"An invalid entry was saved for {string.Join(" ", arguments.Take(6))}: {string.Join(" ", problems)}");
            }

            var entry = RecordEntries.Parse(lines[0]);
            Equal(EntryStatus.Rejected, entry.Status);
            Equal(true, Problems(entry).Count <= 50);
        }
    }

    private static void RefusesADifferentValueUnlessReplaced()
    {
        using var run = new Run();
        Equal(0, run.Record("--name", "ratio", "--value", "1").ExitCode);
        Equal(0, run.Record("--name", "ratio", "--value", "1.0").ExitCode);
        var refused = run.Record("--name", "ratio", "--value", "2");
        Equal(0, refused.ExitCode);
        Equal(true, refused.Output.Contains("already recorded 1 for this dataset", StringComparison.Ordinal));
        Equal(0, run.Record("--name", "ratio", "--value", "2", "--replace").ExitCode);
        Equal(0, run.Record("--name", "ratio", "--value", "2").ExitCode);
        Equal(0, run.Record("--name", "ratio", "--value", "1").ExitCode);
        Equal(
            "recorded 1,recorded 1,rejected,recorded 2 replaces,recorded 2,rejected",
            string.Join(",", run.Entries().Select(entry => entry.Status == EntryStatus.Rejected
                ? "rejected"
                : $"recorded {entry.Value!.Value}{(entry.Replaces ? " replaces" : string.Empty)}")));

        // Another attempt or another job is a different file, so nothing conflicts.
        Equal(0, run.Record("--name", "ratio", "--value", "3", "--attempt", "2").ExitCode);
        Equal(EntryStatus.Recorded, Single(run.Entries(EntriesFile.FileName("pack", 2))).Status);

        var merged = EntryMerge.Merge(Commit, run.Entries(), []);
        Equal("2", Single(merged.Entries.Where(entry => entry.Status == EntryStatus.Recorded)).Value!.Value);

        // Giving the declared unit is the same as leaving it out, so the same value is not refused.
        using var units = new Run();
        Equal(0, units.Record("--name", "warnings", "--value", "1").ExitCode);
        Equal(0, units.Record("--name", "warnings", "--value", "1", "--unit", "warnings").ExitCode);
        Equal("Recorded -,Recorded -", string.Join(",", units.Entries().Select(entry => $"{entry.Status} {entry.Value?.Unit ?? "-"}")));

        // A last line left unfinished is ended before the next entry, so neither entry is lost.
        using var unfinished = new Run();
        Equal(0, unfinished.Record("--name", "ratio", "--value", "1").ExitCode);
        File.WriteAllText(unfinished.EntriesFile, File.ReadAllText(unfinished.EntriesFile).TrimEnd('\n'));
        Equal(0, unfinished.Record("--name", "ratio", "--value", "1").ExitCode);
        File.WriteAllText(unfinished.EntriesFile, File.ReadAllText(unfinished.EntriesFile).TrimEnd('\n'));
        Equal(0, unfinished.Record("--name", "warnings", "--skipped", "--reason", "Not built here.").ExitCode);
        var read = EntriesFile.Read(unfinished.EntriesFile);
        Equal(0, read.Problems.Count);
        Equal(3, read.Entries.Count);
    }

    private static void ReadsTheEnvironment()
    {
        using var run = new Run();
        var runDirectory = Path.Combine(run.Directory, "elsewhere");
        var environment = new Dictionary<string, string>
        {
            ["DIFFRA_PROJECT"] = "main",
            ["DIFFRA_COMMIT"] = Commit,
            ["DIFFRA_JOB"] = "build (linux)",
            ["DIFFRA_JOB_ATTEMPT"] = "3",
            ["DIFFRA_DIMENSION_OS"] = "windows",
            ["DIFFRA_RUN_DIR"] = runDirectory,
            ["DIFFRA_ROOT_FILE"] = run.RootFilePath,
        };
        var result = RunCli(run.Directory, environment, "record", "--name", "packageSize", "--value", "10");
        Equal(0, result.ExitCode);
        var entry = Single(EntriesFile.Read(Path.Combine(runDirectory, "entries", EntriesFile.FileName("build (linux)", 3))).Entries);
        Equal(EntryStatus.Recorded, entry.Status);
        Equal("build (linux)", entry.JobName);
        Equal(3, entry.Attempt);
        Equal("windows", entry.Dimensions["os"]);

        // An option wins over the environment.
        var option = RunCli(run.Directory, environment, "record", "--name", "packageSize", "--value", "10", "--dimension", "os=linux", "--attempt", "4");
        Equal(0, option.ExitCode);
        Equal("linux", Single(EntriesFile.Read(Path.Combine(runDirectory, "entries", EntriesFile.FileName("build (linux)", 4))).Entries).Dimensions["os"]);

        // A dimension variable is read only for the dimensions the dataset is stratified by.
        var unstratified = RunCli(run.Directory, environment, "record", "--name", "ratio", "--value", "1");
        Equal(0, unstratified.ExitCode);
        Equal(EntryStatus.Recorded, EntriesFile.Read(Path.Combine(runDirectory, "entries", EntriesFile.FileName("build (linux)", 3))).Entries[^1].Status);

        // An empty variable or an empty --run-dir counts as not given.
        environment["DIFFRA_JOB_ATTEMPT"] = string.Empty;
        Equal(0, RunCli(run.Directory, environment, "record", "--name", "ratio", "--value", "1", "--run-dir", string.Empty).ExitCode);
        Equal(1, EntriesFile.Read(Path.Combine(runDirectory, "entries", EntriesFile.FileName("build (linux)", 1))).Entries.Count);
    }

    private static void WritesUnderTheGitDirectory()
    {
        using var run = new Run();
        var gitDirectory = Path.Combine(run.Directory, "repository.git", "worktrees", "one");
        Directory.CreateDirectory(gitDirectory);
        var checkout = Path.Combine(run.Directory, "checkout");
        var nested = Path.Combine(checkout, "src", "deep");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(checkout, ".git"), "gitdir: ../repository.git/worktrees/one\n");
        var environment = Context(run);
        Equal(0, RunCli(nested, environment, "record", "--name", "ratio", "--value", "1").ExitCode);
        Equal(1, EntriesFile.Read(Path.Combine(gitDirectory, "diffra", "run", "entries", EntriesFile.FileName("pack", 1))).Entries.Count);

        var plain = Path.Combine(run.Directory, "plain");
        Directory.CreateDirectory(Path.Combine(plain, ".git"));
        Equal(0, RunCli(plain, environment, "record", "--name", "ratio", "--value", "1").ExitCode);
        Equal(1, EntriesFile.Read(Path.Combine(plain, ".git", "diffra", "run", "entries", EntriesFile.FileName("pack", 1))).Entries.Count);

        // A .git file that names no directory gives no run folder, rather than one in the checkout.
        var unnamed = Path.Combine(run.Directory, "unnamed");
        Directory.CreateDirectory(unnamed);
        File.WriteAllText(Path.Combine(unnamed, ".git"), "gitdir:\n");
        Equal(1, RunCli(unnamed, environment, "record", "--name", "ratio", "--value", "1").ExitCode);
        Equal(false, Directory.Exists(Path.Combine(unnamed, "diffra")));
    }

    private static void AnswersInJson()
    {
        using var run = new Run();
        var result = run.Record("--name", "ratio", "--value", "ten", "--json");
        Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Equal("rejected", document.RootElement.GetProperty("status").GetString());
        Equal(run.EntriesFile, document.RootElement.GetProperty("entriesFile").GetString());
        Equal("'ten' is not a number.", document.RootElement.GetProperty("entry").GetProperty("problems")[0].GetString());
    }

    // The size dataset's files are read with the files converter, from the glob in the root file
    // or from --from, relative to the repository root wherever record runs.
    private static void ReadsFilesWithTheConverter()
    {
        using var run = new Run();
        run.MakeRepository();
        run.Write("artifacts/b.nupkg", "1234567");
        run.Write("artifacts/a.nupkg", "12345");
        run.Write("artifacts/notes.txt", "ignored");
        run.Write("other/c.nupkg", "123");

        var result = run.Record("--name", "packages");
        Equal(0, result.ExitCode);
        Equal(true, result.Output.Contains("Recorded main/packages from 2 files, 12 B in total.", StringComparison.Ordinal));
        var line = File.ReadAllLines(run.EntriesFile).Single();
        Equal(0, RecordEntries.Validate(line).Count);
        var entry = RecordEntries.Parse(line);
        Equal(EntryStatus.Recorded, entry.Status);
        Equal(null, entry.Value);
        Equal("""{"files":{"artifacts/a.nupkg":{"value":"5"},"artifacts/b.nupkg":{"value":"7"}},"total":{"value":"12"}}""", entry.Data!.Value.GetRawText());
        using (var document = JsonDocument.Parse(line))
        {
            var sources = document.RootElement.GetProperty("sources").EnumerateArray().ToList();
            Equal(2, sources.Count);
            Equal("artifacts/a.nupkg", sources[0].GetProperty("path").GetString());
            Equal("files", sources[0].GetProperty("format").GetString());
            Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("12345"u8)), sources[0].GetProperty("sha256").GetString());
        }

        // The same files again are the same entry; a changed file is refused unless replaced.
        Equal(0, run.Record("--name", "packages").ExitCode);
        run.Write("artifacts/a.nupkg", "123456");
        Equal(true, run.Record("--name", "packages").Output.Contains("already recorded a different value", StringComparison.Ordinal));
        using (var conflict = JsonDocument.Parse(File.ReadAllLines(run.EntriesFile)[^1]))
        {
            // The rejected entry keeps the files it read, with their digests.
            var kept = conflict.RootElement.GetProperty("sources").EnumerateArray().ToList();
            Equal("artifacts/a.nupkg,artifacts/b.nupkg", string.Join(",", kept.Select(item => item.GetProperty("path").GetString())));
            Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("123456"u8)), kept[0].GetProperty("sha256").GetString());
        }

        Equal(0, run.Record("--name", "packages", "--replace").ExitCode);
        Equal("recorded,recorded,rejected,recorded replaces", string.Join(",", run.Entries().Select(item =>
            RecordEntries.Token(item.Status) + (item.Replaces ? " replaces" : string.Empty))));

        // --from replaces the root file's glob, and the keys stay relative to the repository root
        // when record runs in a folder below it.
        using var other = new Run();
        other.MakeRepository();
        other.Write("other/c.nupkg", "123");
        var nested = Path.Combine(other.Directory, "other");
        var fromOption = RunCli(nested, Context(other), "record", "--run-dir", Path.Combine(other.Directory, "run"), "--name", "packages", "--from", "other/*.nupkg");
        Equal(0, fromOption.ExitCode);
        Equal("""{"files":{"other/c.nupkg":{"value":"3"}},"total":{"value":"3"}}""", Single(other.Entries()).Data!.Value.GetRawText());

        // --json answers with the entry.
        var json = other.Record("--name", "packages", "--from", "other/*.nupkg", "--json");
        using var answer = JsonDocument.Parse(json.Output);
        Equal("recorded", answer.RootElement.GetProperty("status").GetString());
        Equal("3", answer.RootElement.GetProperty("entry").GetProperty("data").GetProperty("total").GetProperty("value").GetString());
    }

    private static void RejectsFilesItCannotRead()
    {
        var cases = new (string[] Arguments, string Problem)[]
        {
            (["--name", "packages", "--from", "dist/*.zip"], "No file matches 'dist/*.zip'."),
            (["--name", "packages", "--from", "../*.nupkg"], "must stay inside the repository"),
            (["--name", "packages", "--from", "/etc/hosts"], "is not relative to the repository root"),
            (["--name", "packages", "--from", "artifacts/*.nupkg", "--dimension", "os=linux"], "is not stratified by 'os'"),
        };
        foreach (var (arguments, problem) in cases)
        {
            using var run = new Run();
            run.MakeRepository();
            run.Write("artifacts/a.nupkg", "1");
            Equal(0, run.Record(arguments).ExitCode);
            var line = File.ReadAllLines(run.EntriesFile).Single();
            Equal(0, RecordEntries.Validate(line).Count);
            var entry = RecordEntries.Parse(line);
            Equal(EntryStatus.Rejected, entry.Status);
            if (!Problems(entry).Any(text => text.Contains(problem, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"Expected a problem containing '{problem}', got: {string.Join(" | ", Problems(entry))}");
            }
        }

        // A rejected entry keeps the sources that were read.
        using var kept = new Run();
        kept.MakeRepository();
        kept.Write("artifacts/a.nupkg", "1");
        Equal(0, kept.Record("--name", "packages", "--dimension", "os=linux").ExitCode);
        using var document = JsonDocument.Parse(File.ReadAllLines(kept.EntriesFile).Single());
        Equal("artifacts/a.nupkg", document.RootElement.GetProperty("sources")[0].GetProperty("path").GetString());

        // A maxKeys budget the files pass rejects the entry, since the entry cannot yet state which
        // keys a budget dropped.
        using var budget = new Run(RootFile.Replace("\ndatasets:", "\nsettings: { limits: { maxKeys: 1 } }\ndatasets:", StringComparison.Ordinal));
        budget.MakeRepository();
        budget.Write("artifacts/a.nupkg", "1");
        budget.Write("artifacts/b.nupkg", "2");
        Equal(0, budget.Record("--name", "packages").ExitCode);
        Equal(true, Problems(Single(budget.Entries()))[0].Contains("maxKeys budget of 1", StringComparison.Ordinal));
    }

    private static List<string> Problems(RecordEntry entry)
    {
        using var document = JsonDocument.Parse(entry.Json);
        return document.RootElement.TryGetProperty("problems", out var problems)
            ? [.. problems.EnumerateArray().Select(problem => problem.GetString()!)]
            : [];
    }

    private static Dictionary<string, string> Context(Run run) => new()
    {
        ["DIFFRA_PROJECT"] = "main",
        ["DIFFRA_COMMIT"] = Commit,
        ["DIFFRA_JOB"] = "pack",
        ["DIFFRA_ROOT_FILE"] = run.RootFilePath,
    };

    private sealed class Run : IDisposable
    {
        public Run(string rootFile = RootFile)
        {
            Directory = Path.Combine(FindRepositoryRoot(), "temp", $"record-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
            RootFilePath = Path.Combine(Directory, "diffra.yml");
            File.WriteAllText(RootFilePath, rootFile);
        }

        public string Directory { get; }

        // Makes the folder a repository root, so that files are read relative to it.
        public void MakeRepository() => System.IO.Directory.CreateDirectory(Path.Combine(Directory, ".git"));

        public void Write(string path, string content)
        {
            var full = Path.Combine(Directory, path);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public string RootFilePath { get; }

        public string EntriesFile => Path.Combine(Directory, "run", "entries", Protocol.EntriesFile.FileName("pack", 1));

        public (int ExitCode, string Output, string Error) Record(params string[] arguments) =>
            Record(withProject: true, arguments);

        public (int ExitCode, string Output, string Error) Record(bool withProject, params string[] arguments) =>
            Record(withProject, withRootFile: true, arguments);

        public (int ExitCode, string Output, string Error) Record(bool withRootFile, string first, params string[] arguments) =>
            Record(withProject: true, withRootFile, [first, .. arguments]);

        private (int ExitCode, string Output, string Error) Record(bool withProject, bool withRootFile, string[] arguments)
        {
            // The context goes first, so that an option left without its value at the end is seen as such.
            var all = new List<string> { "record", "--run-dir", Path.Combine(Directory, "run") };
            if (!arguments.Contains("--commit"))
            {
                all.AddRange(["--commit", Commit]);
            }

            if (!arguments.Contains("--job"))
            {
                all.AddRange(["--job", "pack"]);
            }

            if (withProject && !arguments.Contains("--project"))
            {
                all.AddRange(["--project", "main"]);
            }

            if (withRootFile && !arguments.Contains("--root-file"))
            {
                all.AddRange(["--root-file", RootFilePath]);
            }

            all.AddRange(arguments);
            return RunCli(Directory, new Dictionary<string, string>(), [.. all]);
        }

        public IReadOnlyList<RecordEntry> Entries(string? fileName = null)
        {
            var content = Protocol.EntriesFile.Read(fileName is null ? EntriesFile : Path.Combine(Directory, "run", "entries", fileName));
            Equal(0, content.Problems.Count);
            return content.Entries;
        }

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

    internal static (int ExitCode, string Output, string Error) RunCli(string workingDirectory, Dictionary<string, string> environment, params string[] arguments)
    {
        // The CLI built in the same configuration as these tests.
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent?.Name ?? "Debug";
        var cliAssembly = Path.Combine(FindRepositoryRoot(), "src", "Cli", "bin", configuration, "net10.0", "Diffra.Cli.dll");
        if (!File.Exists(cliAssembly))
        {
            throw new FileNotFoundException("Build Diffra.slnx before running RecordCommandTests.", cliAssembly);
        }

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var name in startInfo.Environment.Keys.Where(name => name.StartsWith("DIFFRA_", StringComparison.Ordinal)).ToList())
        {
            startInfo.Environment.Remove(name);
        }

        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        startInfo.ArgumentList.Add(cliAssembly);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Diffra CLI process.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Result, error);
    }

    private static T Single<T>(IEnumerable<T> items)
    {
        var list = items.ToList();
        Equal(1, list.Count);
        return list[0];
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
