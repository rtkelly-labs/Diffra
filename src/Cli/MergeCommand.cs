using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Diffra.Protocol;
using Diffra.RootFile;

namespace Diffra.Cli;

/// <summary>
/// <c>diffra merge</c>: the collecting job's step. It reads the entries file of every job, merges
/// them by the rules in docs/record.md, writes the merged entries and reports what the merge found.
/// What it finds is reported. It fails the step, with exit code 3, only when the adopter sets
/// <c>fail-on</c> and a finding counts towards it; otherwise it exits non-zero only when it has no
/// usable commit, root file, folder or output path, or cannot write its output.
/// </summary>
internal static class MergeCommand
{
    // Entries files come from other jobs and are read as untrusted data, within these limits.
    private const int MaximumFiles = 1000;
    private const long MaximumFileBytes = 16L * 1024 * 1024;
    private const long MaximumTotalBytes = 256L * 1024 * 1024;
    private const int MaximumProblemsPerFile = 20;
    private const int MaximumInvalidLinesPerFile = 100;
    private const int MaximumProblems = 1000;
    private const int MaximumReported = 1000;

    internal const string Usage = "Usage: merge [--commit <id>] [--root-file <file>] [--run-dir <dir>] [--entries <dir>]... [-o <file>] [--fail-on never|fail|warn] [--json]";

    private static readonly string[] Valued = ["commit", "root-file", "run-dir", "entries", "o", "fail-on"];
    private static readonly string[] Once = ["commit", "root-file", "run-dir", "o", "fail-on"];
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = ReportEncoder(), Indented = true };

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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

        // A misspelt setting is an error, so that it never turns enforcement off without a word.
        var failOnText = given.TryGetValue("fail-on", out var failOnValues) ? failOnValues[0] : Environment.GetEnvironmentVariable("DIFFRA_FAIL_ON");
        if (!Enforcement.TryParse(failOnText, out var failOn))
        {
            return Fail($"fail-on must be never, fail or warn, not '{Shown(failOnText ?? string.Empty)}'.");
        }

        var rootProblems = new List<string>();
        var rootFile = RecordCommand.LoadRootFile(given, rootProblems);
        if (rootFile is null)
        {
            return Fail(rootProblems[0]);
        }

        List<string> folders;
        string output;
        try
        {
            // The run folder is needed only for what --entries and -o do not give.
            var runDirectory = string.Empty;
            var needsRunDirectory = !given.ContainsKey("entries") || !given.ContainsKey("o");
            if (needsRunDirectory && !RecordCommand.TryRunDirectory(given, out runDirectory, out failure))
            {
                return Fail(failure);
            }

            // No job may have recorded anything, so a missing default folder is not an error.
            folders = given.TryGetValue("entries", out var stated)
                ? [.. stated.Select(Folder).Distinct(StringComparer.FromComparison(PathComparison))]
                : [Path.Combine(runDirectory, "entries")];
            output = given.TryGetValue("o", out var outputs) ? Path.GetFullPath(outputs[0]) : Path.Combine(runDirectory, "merged.jsonl");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Fail($"A path is not valid: {Shown(exception.Message)}");
        }

        if (given.ContainsKey("entries") && folders.Find(folder => !Directory.Exists(folder)) is { } missing)
        {
            return Fail($"The entries folder '{Shown(missing)}' does not exist.");
        }

        // The output must be a file, and must not be read back as an entries file by a later merge.
        var outputFolder = Path.GetDirectoryName(output);
        if (outputFolder is null || Path.EndsInDirectorySeparator(output) || Directory.Exists(output))
        {
            return Fail($"The output '{Shown(output)}' is not a file path.");
        }

        // Links are resolved, so a path through a link to an entries folder or the root file is caught.
        try
        {
            var realOutputFolder = RealPath(outputFolder);
            if (folders.Exists(folder => string.Equals(realOutputFolder, RealPath(folder), PathComparison)))
            {
                return Fail($"The output '{Shown(output)}' is inside an entries folder; write it elsewhere.");
            }

            var rootPath = given.TryGetValue("root-file", out var roots) ? roots[0] : Environment.GetEnvironmentVariable("DIFFRA_ROOT_FILE");
            if (!string.IsNullOrEmpty(rootPath)
                && string.Equals(RealPath(output), RealPath(rootPath), PathComparison))
            {
                return Fail($"The output '{Shown(output)}' is the root file; write it elsewhere.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail($"A path cannot be checked: {exception.Message}");
        }

        try
        {
            var problems = new Problems();
            var (entries, files) = ReadAll(folders, problems);
            var result = EntryMerge.Merge(commit, entries, Expected(rootFile));
            WriteAtomically(output, result.Entries);
            var failsStep = Enforcement.MergeFailsStep(failOn, result.Findings);
            Report(commit, output, files, result, problems.Listed, json, failOn, failsStep);
            if (failsStep)
            {
                var counted = result.Findings.Count(finding => Enforcement.Status(finding.Kind) is not null);
                Console.Error.WriteLine($"merge fails the step: fail-on is {Enforcement.Token(failOn)} and {Count(counted, "finding counts", "findings count")} towards it.");
                return 3;
            }

            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"merge cannot read the entries or write '{Shown(output)}': {Shown(exception.Message)}");
            return 1;
        }
    }

    // What the root file plans for: every required dataset of every project, once for each
    // combination of the listed values of the dimensions it is stratified by. An observed dimension
    // lists no values, so a plan leaves it out and an entry with any value for it matches.
    private static List<ExpectedEntry> Expected(RootFileDocument rootFile)
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

                IEnumerable<Dictionary<string, string>> combinations = [new(StringComparer.Ordinal)];
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

            // -o is the only short option; --o is not another spelling of it.
            var name = argument == "-o" ? "o" : argument.StartsWith("--", StringComparison.Ordinal) && argument != "--o" ? argument[2..] : null;
            if (name is null || !Valued.Contains(name, StringComparer.Ordinal))
            {
                failure = argument.StartsWith('-') ? $"Unknown option '{Shown(argument)}'." : $"Unexpected argument '{Shown(argument)}'.";
                return false;
            }

            if (index + 1 >= args.Length || args[index + 1].Length == 0)
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
        commit = given.TryGetValue("commit", out var values) ? values[0] : Environment.GetEnvironmentVariable("DIFFRA_COMMIT") ?? string.Empty;
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

    // Every file whose name ends in .jsonl directly in each folder, in name order, and every such
    // name counts toward the file limit. Links are not followed; a file is read through one handle
    // within the size limits, and a file with the same bytes as one already read is not read again.
    // Entries files come from an artifact download, which holds regular files only, so other kinds
    // of file, such as named pipes, are not looked for.
    private static (List<RecordEntry> Entries, int Files) ReadAll(List<string> folders, Problems problems)
    {
        var contents = new List<(string File, IReadOnlyList<RecordEntry> Entries)>();
        var read = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = 0;
        var total = 0L;
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            // Only the first names, in order, that are still within the file limit are kept while
            // the folder is listed, so a folder of very many files costs no more than the limit.
            var room = MaximumFiles - seen;
            var candidates = new SortedSet<string>(StringComparer.Ordinal);
            var extra = 0;
            foreach (var name in Directory.EnumerateFiles(folder, "*.jsonl").Select(Path.GetFileName))
            {
                if (name is null || !name.EndsWith(".jsonl", StringComparison.Ordinal))
                {
                    continue;
                }

                candidates.Add(name);
                if (candidates.Count > room)
                {
                    candidates.Remove(candidates.Max!);
                    extra++;
                }
            }

            if (extra > 0)
            {
                problems.Add(folder, $"More than {MaximumFiles.ToString(CultureInfo.InvariantCulture)} entries files were found; {extra.ToString(CultureInfo.InvariantCulture)} were not read.");
            }

            seen += candidates.Count;
            foreach (var file in candidates.Select(name => new FileInfo(Path.Combine(folder, name))))
            {

                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    problems.Add(file.FullName, "The file is a link and was not read.");
                    continue;
                }

                byte[]? bytes;
                try
                {
                    bytes = ReadBounded(file.FullName, Math.Min(MaximumFileBytes, MaximumTotalBytes - total));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    problems.Add(file.FullName, $"The file cannot be read: {exception.Message}");
                    continue;
                }

                if (bytes is null)
                {
                    problems.Add(file.FullName, MaximumTotalBytes - total < MaximumFileBytes
                        ? $"The entries files are over the total limit of {MaximumTotalBytes.ToString(CultureInfo.InvariantCulture)} bytes; this one was not read."
                        : $"The file is over the limit of {MaximumFileBytes.ToString(CultureInfo.InvariantCulture)} bytes and was not read.");
                    continue;
                }

                var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (read.TryGetValue(digest, out var earlier))
                {
                    problems.Add(file.FullName, $"The file has the same content as '{earlier}' and was not read again.");
                    continue;
                }

                read[digest] = file.FullName;
                total += bytes.Length;
                EntriesFileContent content;
                try
                {
                    content = EntriesFile.Read(bytes, MaximumInvalidLinesPerFile);
                }
                catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
                {
                    problems.Add(file.FullName, $"The file cannot be read as entries: {exception.Message}");
                    continue;
                }

                contents.Add((file.FullName, content.Entries));
                foreach (var problem in content.Problems.Take(MaximumProblemsPerFile))
                {
                    problems.Add(file.FullName, problem);
                }

                if (content.Problems.Count > MaximumProblemsPerFile)
                {
                    problems.Add(file.FullName, $"{(content.Problems.Count - MaximumProblemsPerFile).ToString(CultureInfo.InvariantCulture)} more lines were not valid entries.");
                }

                if (content.Stopped)
                {
                    problems.Add(file.FullName, $"Reading stopped after {MaximumInvalidLinesPerFile.ToString(CultureInfo.InvariantCulture)} lines that were not valid entries.");
                }
            }
        }

        return (Distinct(contents, problems), read.Count);
    }

    // Each job attempt writes one entries file, so a file whose entries are the first entries of
    // another file, in the same order, is a copy of it written less far, such as an artifact
    // uploaded twice. It is reported and left out, so that the fuller copy does not conflict with
    // it. Any other files, even for the same job attempt, are all merged, and the merge reports
    // where they disagree.
    private static List<RecordEntry> Distinct(List<(string File, IReadOnlyList<RecordEntry> Entries)> contents, Problems problems)
    {
        var kept = new List<(string File, IReadOnlyList<RecordEntry> Entries)>();
        var byFirst = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, entries) in contents.OrderByDescending(content => content.Entries.Count))
        {
            if (entries.Count > 0 && byFirst.TryGetValue(entries[0].Json, out var candidates)
                && candidates.Select(index => kept[index]).FirstOrDefault(fuller => IsPrefix(entries, fuller.Entries)) is { File: { } owner })
            {
                problems.Add(file, $"The file's entries are the first entries of '{owner}', so it is an earlier copy of that file and was not merged.");
                dropped.Add(file);
                continue;
            }

            if (entries.Count > 0)
            {
                if (!byFirst.TryGetValue(entries[0].Json, out var list))
                {
                    byFirst[entries[0].Json] = list = [];
                }

                list.Add(kept.Count);
            }

            kept.Add((file, entries));
        }

        return [.. contents.Where(content => !dropped.Contains(content.File)).SelectMany(content => content.Entries)];
    }

    private static bool IsPrefix(IReadOnlyList<RecordEntry> shorter, IReadOnlyList<RecordEntry> longer)
    {
        if (shorter.Count > longer.Count)
        {
            return false;
        }

        for (var index = 0; index < shorter.Count; index++)
        {
            if (!string.Equals(shorter[index].Json, longer[index].Json, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // The JSON report escapes the characters the plain report escapes, and the characters the default
    // encoder escapes, such as quotes and angle brackets.
    private static JavaScriptEncoder ReportEncoder()
    {
        var settings = new TextEncoderSettings(UnicodeRanges.All);
        settings.ForbidCharacters('\u061c', '\u200e', '\u200f', '\u2028', '\u2029', '\u202a', '\u202b', '\u202c', '\u202d', '\u202e', '\u2066', '\u2067', '\u2068', '\u2069');
        return JavaScriptEncoder.Create(settings);
    }

    // The full path with every link in it resolved, without a trailing separator. A link loop or
    // a chain longer than 40 links throws IOException.
    private static string RealPath(string path, int depth = 0)
    {
        if (depth > 40)
        {
            throw new IOException($"Too many links in '{path}'.");
        }

        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full) ?? string.Empty;
        foreach (var segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                next = RealPath(target.FullName, depth + 1);
            }

            current = next;
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    // At most `limit` bytes, read through one handle so that a file that grows after it was listed
    // cannot pass the limit; null when the file is longer.
    private static byte[]? ReadBounded(string path, long limit)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = stream.Read(chunk)) > 0)
        {
            buffer.Write(chunk, 0, count);
            if (buffer.Length > limit)
            {
                return null;
            }
        }

        return buffer.ToArray();
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
        bool json,
        FailOn failOn,
        bool failsStep)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (!json)
        {
            // Names, values and messages come from other jobs' files, so control characters are
            // escaped: a line break in them must not start a line that reads as a finding, or as a
            // command to the CI host.
            Console.WriteLine($"Merged {Count(result.Entries.Count, "entry", "entries")} from {Count(files, "entries file", "entries files")} for commit {commit} into {Escape(output)}.");
            foreach (var attempt in result.SupersededAttempts.Take(MaximumReported))
            {
                Console.WriteLine($"Superseded: {Escape(attempt)}");
            }

            if (result.SupersededAttempts.Count > MaximumReported)
            {
                Console.WriteLine($"{(result.SupersededAttempts.Count - MaximumReported).ToString(CultureInfo.InvariantCulture)} more superseded attempts are not listed.");
            }

            foreach (var finding in result.Findings.Take(MaximumReported))
            {
                Console.WriteLine($"{Kind(finding.Kind)}: {Escape(finding.Message)}");
            }

            if (result.Findings.Count > MaximumReported)
            {
                Console.WriteLine($"{(result.Findings.Count - MaximumReported).ToString(CultureInfo.InvariantCulture)} more findings are not listed.");
            }

            foreach (var (file, message) in problems)
            {
                Console.WriteLine(file.Length == 0 ? $"unreadable: {Escape(message)}" : $"unreadable: {Escape(file)}: {Escape(message)}");
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
            writer.WriteString("failOn", Enforcement.Token(failOn));
            writer.WriteBoolean("failsStep", failsStep);
            writer.WriteStartArray("supersededAttempts");
            foreach (var attempt in result.SupersededAttempts.Take(MaximumReported))
            {
                writer.WriteStringValue(attempt);
            }

            writer.WriteEndArray();
            writer.WriteNumber("supersededAttemptsNotListed", Math.Max(0, result.SupersededAttempts.Count - MaximumReported));
            writer.WriteStartArray("findings");
            foreach (var finding in result.Findings.Take(MaximumReported))
            {
                writer.WriteStartObject();
                writer.WriteString("kind", Kind(finding.Kind));
                writer.WriteString("message", finding.Message);
                if (Enforcement.Status(finding.Kind) is { } status)
                {
                    writer.WriteString("countsAs", status);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("findingsNotListed", Math.Max(0, result.Findings.Count - MaximumReported));
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

    // Control characters, and the Unicode line and paragraph separators, written as \uXXXX.
    private static string Escape(string text)
    {
        if (!text.Any(NeedsEscape))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 16);
        foreach (var character in text)
        {
            if (NeedsEscape(character))
            {
                builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    // Also the bidirectional controls, which can make a line read as other text.
    private static bool NeedsEscape(char character) =>
        char.IsControl(character) || character is '\u2028' or '\u2029' or (>= '\u202a' and <= '\u202e') or (>= '\u2066' and <= '\u2069') or '\u200e' or '\u200f' or '\u061c';

    // A folder's full path without a trailing separator, so that 'd' and 'd/' are one folder.
    private static string Folder(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // Text from the command line or the file system, cut and escaped for an error message.
    private static string Shown(string text) => Escape(text.Length > 300 ? text[..300] + "..." : text);

    private static string Kind(MergeFindingKind kind) => kind switch
    {
        MergeFindingKind.OtherCommit => "other-commit",
        MergeFindingKind.Conflict => "conflict",
        MergeFindingKind.StatusConflict => "status-conflict",
        MergeFindingKind.ValueAndData => "value-and-data",
        MergeFindingKind.NotCollected => "not-collected",
        MergeFindingKind.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string Count(int count, string one, string many) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? one : many)}";

    private static int Fail(string message)
    {
        Console.Error.WriteLine(Escape(message));
        Console.Error.WriteLine(Usage);
        return 2;
    }

    // The problems found while reading, up to a limit, then one line saying that there were more.
    private sealed class Problems
    {
        private bool _omitted;

        public List<(string File, string Message)> Listed { get; } = [];

        public void Add(string file, string message)
        {
            if (Listed.Count < MaximumProblems)
            {
                Listed.Add((file, message));
            }
            else if (!_omitted)
            {
                _omitted = true;
                Listed.Add((string.Empty, "More problems were found than are listed."));
            }
        }
    }
}
