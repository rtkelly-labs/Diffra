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
        Equal(false, File.Exists(run.Output));
    }

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
            var result = RecordCommandTests.RunCli(
                Directory,
                new Dictionary<string, string>(),
                ["record", .. arguments, "--project", "main", "--commit", Commit, "--job", job, "--attempt", attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
