using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Diffra.Protocol;
using Diffra.RootFile;

namespace Diffra.Cli;

/// <summary>
/// <c>diffra merge</c>: the collecting job's step. It reads the entries file of every job, merges
/// them by the rules in docs/record.md, writes the merged entries and reports what the merge found.
/// What it finds is reported and never fails the command; it exits non-zero only when it has no
/// usable commit, root file or folder, or cannot write its output.
/// </summary>
internal static class MergeCommand
{
    // Entries files come from other jobs and are read as untrusted data, within these limits.
    private const int MaximumFiles = 1000;
    private const long MaximumFileBytes = 16L * 1024 * 1024;
    private const long MaximumTotalBytes = 256L * 1024 * 1024;

    internal const string Usage = "Usage: merge [--commit <id>] [--root-file <file>] [--run-dir <dir>] [--entries <dir>]... [-o <file>] [--json]";

    private static readonly string[] Valued = ["commit", "root-file", "run-dir", "entries", "o"];
    private static readonly string[] Once = ["commit", "root-file", "run-dir", "o"];
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = true };

    public static int Run(string[] args)
    {
        if (args is ["--help" or "-h"])
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (!TryParse(args, out var given, out var json, out var failure)
            || !TryCommit(given, out var commit, out failure))
        {
            return Fail(failure);
        }

        var rootProblems = new List<string>();
        var rootFile = RecordCommand.LoadRootFile(given, rootProblems);
        if (rootFile is null)
        {
            return Fail(rootProblems[0]);
        }

        // The run folder is needed only for what --entries and -o do not give.
        var runDirectory = string.Empty;
        var needsRunDirectory = !given.ContainsKey("entries") || !given.ContainsKey("o");
        if (needsRunDirectory && !RecordCommand.TryRunDirectory(given, out runDirectory, out failure))
        {
            return Fail(failure);
        }

        List<string> folders;
        if (given.TryGetValue("entries", out var stated))
        {
            folders = [.. stated.Select(Path.GetFullPath)];
            if (folders.Find(folder => !Directory.Exists(folder)) is { } missing)
            {
                return Fail($"The entries folder '{missing}' does not exist.");
            }
        }
        else
        {
            // No job may have recorded anything, so a missing default folder is not an error.
            folders = [Path.Combine(runDirectory, "entries")];
        }

        var output = given.TryGetValue("o", out var outputs) ? Path.GetFullPath(outputs[0]) : Path.Combine(runDirectory, "merged.jsonl");
        try
        {
            var problems = new List<(string File, string Message)>();
            var (entries, files) = ReadAll(folders, problems);
            var result = EntryMerge.Merge(commit, entries, Expected(rootFile));
            WriteAtomically(output, result.Entries);
            Report(commit, output, files, result, problems, json);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"merge cannot read the entries or write '{output}': {exception.Message}");
            return 1;
        }
    }

    // What the root file plans for: every required dataset of every project, once for each
    // combination of the listed values of the dimensions it is stratified by. An observed dimension
    // lists no values, so a plan leaves it out and an entry with any value for it matches.
    internal static List<ExpectedEntry> Expected(RootFileDocument rootFile)
    {
        var expected = new List<ExpectedEntry>();
        foreach (var project in rootFile.Projects.Values.OrderBy(project => project.Name, StringComparer.Ordinal))
        {
            foreach (var name in project.Datasets.Keys.Order(StringComparer.Ordinal))
            {
                if (!rootFile.Datasets.TryGetValue(name, out var dataset) || !dataset.Required)
                {
                    continue;
                }

                IEnumerable<Dictionary<string, string>> combinations = [new Dictionary<string, string>(StringComparer.Ordinal)];
                foreach (var dimension in dataset.Stratify)
                {
                    if (rootFile.Dimensions.TryGetValue(dimension, out var declared) && declared.Values is { } values)
                    {
                        combinations = combinations
                            .SelectMany(combination => values.Select(value => new Dictionary<string, string>(combination, StringComparer.Ordinal) { [dimension] = value }))
                            .ToList();
                    }
                }

                expected.AddRange(combinations.Select(combination => new ExpectedEntry(project.Name, name, combination)));
            }
        }

        return expected;
    }

    private static bool TryParse(string[] args, out Dictionary<string, List<string>> given, out bool json, out string failure)
    {
        given = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        json = false;
        failure = string.Empty;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument == "--json")
            {
                json = true;
                continue;
            }

            var name = argument == "-o" ? "o" : argument.StartsWith("--", StringComparison.Ordinal) ? argument[2..] : null;
            if (name is null || !Valued.Contains(name, StringComparer.Ordinal))
            {
                failure = argument.StartsWith('-') ? $"Unknown option '{argument}'." : $"Unexpected argument '{argument}'.";
                return false;
            }

            if (index + 1 >= args.Length)
            {
                failure = $"{argument} needs a value.";
                return false;
            }

            if (!given.TryGetValue(name, out var values))
            {
                given[name] = values = [];
            }
            else if (Once.Contains(name, StringComparer.Ordinal))
            {
                failure = $"{argument} was given more than once.";
                return false;
            }

            values.Add(args[++index]);
        }

        return true;
    }

    private static bool TryCommit(Dictionary<string, List<string>> given, out string commit, out string failure)
    {
        failure = string.Empty;
        commit = given.TryGetValue("commit", out var values) ? values[0] : System.Environment.GetEnvironmentVariable("DIFFRA_COMMIT") ?? string.Empty;
        if (commit.Length == 0)
        {
            failure = "merge needs a commit: give --commit or set DIFFRA_COMMIT. It is not read from the checkout.";
            return false;
        }

        if (!RecordCommand.CommitPattern().IsMatch(commit))
        {
            failure = "The commit must be a full commit ID: 40 or 64 lowercase hexadecimal characters.";
            return false;
        }

        return true;
    }

    // Every *.jsonl file directly in each folder, in name order. Links and other kinds of file are
    // not followed, and a file or a total over the limits is reported and not read.
    private static (List<RecordEntry> Entries, int Files) ReadAll(List<string> folders, List<(string File, string Message)> problems)
    {
        var entries = new List<RecordEntry>();
        var files = 0;
        var total = 0L;
        foreach (var folder in folders.Distinct(StringComparer.Ordinal))
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.jsonl").OrderBy(file => file.Name, StringComparer.Ordinal))
            {
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    problems.Add((file.FullName, "The file is a link and was not read."));
                    continue;
                }

                if (files == MaximumFiles)
                {
                    problems.Add((file.FullName, $"More than {MaximumFiles.ToString(CultureInfo.InvariantCulture)} entries files were found; this one and the rest were not read."));
                    return (entries, files);
                }

                if (file.Length > MaximumFileBytes || total + file.Length > MaximumTotalBytes)
                {
                    problems.Add((file.FullName, "The file is over the size limit and was not read."));
                    continue;
                }

                total += file.Length;
                files++;
                var content = EntriesFile.Read(file.FullName);
                entries.AddRange(content.Entries);
                problems.AddRange(content.Problems.Select(problem => (file.FullName, problem)));
            }
        }

        return (entries, files);
    }

    private static void WriteAtomically(string path, IReadOnlyList<RecordEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                foreach (var entry in entries)
                {
                    stream.Write(Encoding.UTF8.GetBytes(entry.Json + "\n"));
                }

                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void Report(
        string commit,
        string output,
        int files,
        MergeResult result,
        List<(string File, string Message)> problems,
        bool json)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (!json)
        {
            Console.WriteLine($"Merged {Count(result.Entries.Count, "entry", "entries")} from {Count(files, "entries file", "entries files")} for commit {commit} into {output}.");
            foreach (var attempt in result.SupersededAttempts)
            {
                Console.WriteLine($"Superseded: {attempt}");
            }

            foreach (var finding in result.Findings)
            {
                Console.WriteLine($"{Kind(finding.Kind)}: {finding.Message}");
            }

            foreach (var (file, message) in problems)
            {
                Console.WriteLine($"unreadable: {file}: {message}");
            }

            return;
        }

        using var stream = Console.OpenStandardOutput();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("commit", commit);
            writer.WriteString("entriesFile", output);
            writer.WriteNumber("entries", result.Entries.Count);
            writer.WriteNumber("entriesFiles", files);
            writer.WriteStartArray("supersededAttempts");
            foreach (var attempt in result.SupersededAttempts)
            {
                writer.WriteStringValue(attempt);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("findings");
            foreach (var finding in result.Findings)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", Kind(finding.Kind));
                writer.WriteString("message", finding.Message);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("unreadable");
            foreach (var (file, message) in problems)
            {
                writer.WriteStartObject();
                writer.WriteString("file", file);
                writer.WriteString("message", message);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        stream.Write("\n"u8);
    }

    private static string Kind(MergeFindingKind kind) => kind switch
    {
        MergeFindingKind.OtherCommit => "other-commit",
        MergeFindingKind.Conflict => "conflict",
        MergeFindingKind.StatusConflict => "status-conflict",
        MergeFindingKind.ValueAndData => "value-and-data",
        MergeFindingKind.NotCollected => "not-collected",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string Count(int count, string one, string many) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? one : many)}";

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 2;
    }
}
