using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Diffra.Protocol;
using Diffra.RootFile;

namespace Diffra.Cli;

/// <summary>
/// <c>diffra record</c>: appends one entry for one dataset to the job's entries file
/// (docs/record.md). Anything wrong with what was given is saved as a rejected entry, so the final
/// step can report every problem together; the command exits non-zero only when no entry can be
/// saved at all.
/// </summary>
internal static partial class RecordCommand
{
    private const int MaximumGiven = 200;
    private const int MaximumTrimmed = 1000;
    private const int MaximumRaw = 2000;
    private const int MaximumText = 2000;
    private const int MaximumProblems = 50;
    private const int MaximumRepeats = 100;
    private const int MaximumValueLength = 100;
    private const int MaximumExponent = 100;

    internal const string Usage = "Usage: record --name <dataset> [--value <number> [--unit <unit>] [--key <key>] [--replace] | --from <path-or-glob> | --skipped --reason <text> | --failed --reason <text>] "
        + "[--project <name>] [--commit <id>] [--job <name>] [--attempt <n>] [--dimension <name>=<value>]... [--root-file <file>] [--run-dir <dir>] [--json]";

    private static readonly string[] Flags = ["skipped", "failed", "replace", "json"];

    private static readonly string[] Valued =
        ["name", "value", "unit", "from", "reason", "project", "key", "commit", "job", "attempt", "dimension", "root-file", "run-dir"];

    private static readonly string[] ContextOptions = ["name", "project", "commit", "job", "attempt"];
    private static readonly string[] Once = ["value", "unit", "from", "reason", "key", "skipped", "failed", "replace", "json"];
    private static readonly string[] Sources = ["value", "from", "skipped", "failed"];
    private static readonly string[] ValueOnly = ["unit", "key", "replace"];

    private static readonly string[] Numeric = ["count", "integer", "decimal", "bytes", "duration", "ratio"];

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [GeneratedRegex("^(?:[0-9a-f]{40}|[0-9a-f]{64})\\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    [GeneratedRegex("^([+-]?)([0-9]*)(?:\\.([0-9]*))?(?:[eE]([+-]?[0-9]{1,4}))?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    /// <summary>Runs the command and returns its exit code: 0 when an entry was saved, rejected or not.</summary>
    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args is ["--help" or "-h"])
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var problems = new List<string>();
        var given = Parse(args, problems);

        // What the entry is saved under. Without these there is nothing to save, so the command fails.
        if (!TryContext(given, out var context, out var failure))
        {
            Console.Error.WriteLine(failure);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (!TryRunDirectory(given, out var runDirectory, out failure))
        {
            Console.Error.WriteLine(failure);
            return 1;
        }

        // Everything else is checked in full, and a problem is saved as a rejected entry.
        var json = given.ContainsKey("json");
        var document = LoadRootFile(given, problems);
        var dimensions = ReadDimensions(given, context.Dataset, document, problems);
        var source = Check(given, context, document, dimensions, problems);
        var entry = problems.Count == 0
            ? Stated(context, dimensions, source)
            : Rejected(context, dimensions, given, problems);

        var path = Path.Combine(runDirectory, "entries", EntriesFile.FileName(context.Job, context.Attempt));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (problems.Count == 0 && source.Status == EntryStatus.Recorded)
            {
                string? rejection = null;
                if (!EntriesFile.AppendUnlessConflicting(path, entry, held => rejection = Conflicting(context, dimensions, given, held)))
                {
                    entry = rejection!;
                }
            }
            else
            {
                EntriesFile.Append(path, entry);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine($"The entries file '{path}' cannot be written: {exception.Message}");
            return 1;
        }

        Report(RecordEntries.Parse(entry), path, json);
        return 0;
    }

    private sealed record Context(string Dataset, string Project, string Commit, string Job, int Attempt);

    private sealed record Source(EntryStatus Status, string? Value, string? Unit, string? Key, string? Reason, bool Replaces);

    // Every option, by name without the dashes, with its values in the order given. A flag has
    // one empty value per use.
    private static Dictionary<string, List<string>> Parse(string[] args, List<string> problems)
    {
        var given = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var token = Clean(args[index]);
            var name = token.StartsWith("--", StringComparison.Ordinal) ? token[2..] : null;
            if (name is not null && Array.IndexOf(Flags, name) >= 0)
            {
                Add(given, name, string.Empty);
            }
            else if (name is not null && Array.IndexOf(Valued, name) >= 0)
            {
                if (index + 1 < args.Length)
                {
                    Add(given, name, Clean(args[++index]));
                }
                else
                {
                    problems.Add($"--{name} needs a value.");
                }
            }
            else if (name is not null)
            {
                problems.Add($"Unknown option '{Cut(token, 200)}'.");
            }
            else
            {
                problems.Add($"Unexpected argument '{Cut(token, 200)}'.");
            }
        }

        return given;
    }

    private static void Add(Dictionary<string, List<string>> given, string name, string value)
    {
        if (!given.TryGetValue(name, out var values))
        {
            given[name] = values = [];
        }

        values.Add(value);
    }

    private static bool TryContext(Dictionary<string, List<string>> given, out Context context, out string failure)
    {
        context = new Context(string.Empty, string.Empty, string.Empty, string.Empty, 1);
        foreach (var name in ContextOptions)
        {
            if (given.TryGetValue(name, out var values) && values.Count > 1)
            {
                failure = $"--{name} was given more than once.";
                return false;
            }
        }

        var dataset = First(given, "name");
        if (string.IsNullOrEmpty(dataset) || Length(dataset) > MaximumGiven)
        {
            failure = dataset is null ? "record needs --name, the dataset to record." : $"The dataset name must be 1 to {MaximumGiven} characters.";
            return false;
        }

        var project = First(given, "project") ?? Environment("DIFFRA_PROJECT");
        if (string.IsNullOrEmpty(project) || Length(project) > MaximumGiven)
        {
            failure = project is null ? "record needs a project: give --project or set DIFFRA_PROJECT." : $"The project name must be 1 to {MaximumGiven} characters.";
            return false;
        }

        var commit = First(given, "commit") ?? Environment("DIFFRA_COMMIT");
        if (commit is null || !CommitPattern().IsMatch(commit))
        {
            failure = commit is null
                ? "record needs a commit: give --commit or set DIFFRA_COMMIT. It is not read from the checkout."
                : "The commit must be a full commit ID: 40 or 64 lower-case hexadecimal characters.";
            return false;
        }

        var job = First(given, "job") ?? Environment("DIFFRA_JOB");
        if (job is null || !IsTrimmed(job) || Length(job) > MaximumGiven)
        {
            failure = job is null
                ? "record needs a job name: give --job or set DIFFRA_JOB."
                : $"The job name must be 1 to {MaximumGiven} characters, with no surrounding whitespace and no control characters.";
            return false;
        }

        var attemptText = First(given, "attempt") ?? Environment("DIFFRA_JOB_ATTEMPT") ?? "1";
        if (!int.TryParse(attemptText, NumberStyles.None, CultureInfo.InvariantCulture, out var attempt) || attempt < 1)
        {
            failure = $"The job attempt '{Cut(attemptText, 200)}' is not a positive whole number.";
            return false;
        }

        context = new Context(dataset, project, commit, job, attempt);
        failure = string.Empty;
        return true;
    }

    private static bool TryRunDirectory(Dictionary<string, List<string>> given, out string directory, out string failure)
    {
        directory = string.Empty;
        failure = string.Empty;
        if (given.TryGetValue("run-dir", out var values) && values.Count > 1)
        {
            failure = "--run-dir was given more than once.";
            return false;
        }

        var stated = First(given, "run-dir") ?? Environment("DIFFRA_RUN_DIR");
        if (!string.IsNullOrEmpty(stated))
        {
            directory = Path.GetFullPath(stated);
            return true;
        }

        var gitDirectory = FindGitDirectory(Directory.GetCurrentDirectory());
        if (gitDirectory is null)
        {
            failure = "No run folder: give --run-dir or set DIFFRA_RUN_DIR, or run inside a Git repository.";
            return false;
        }

        directory = Path.Combine(gitDirectory, "diffra", "run");
        return true;
    }

    // The repository's Git directory. In a linked worktree, .git is a file that names it.
    private static string? FindGitDirectory(string start)
    {
        for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            if (File.Exists(candidate))
            {
                var line = File.ReadLines(candidate).FirstOrDefault() ?? string.Empty;
                const string prefix = "gitdir:";
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return Path.GetFullPath(line[prefix.Length..].Trim(), current.FullName);
                }

                return null;
            }
        }

        return null;
    }

    private static RootFileDocument? LoadRootFile(Dictionary<string, List<string>> given, List<string> problems)
    {
        if (given.TryGetValue("root-file", out var values) && values.Count > 1)
        {
            problems.Add("--root-file was given more than once.");
            return null;
        }

        var path = First(given, "root-file") ?? Environment("DIFFRA_ROOT_FILE");
        if (string.IsNullOrEmpty(path))
        {
            problems.Add("No root file was given: give --root-file or set DIFFRA_ROOT_FILE.");
            return null;
        }

        RootFileLoadResult result;
        try
        {
            result = RootFileLoader.LoadFile(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            problems.Add($"The root file '{Cut(path, 500)}' cannot be read: {exception.Message}");
            return null;
        }

        if (result.Document is null)
        {
            var more = result.Problems.Count > 1 ? $" ({result.Problems.Count - 1} more)" : string.Empty;
            problems.Add($"The root file '{Cut(path, 500)}' is not valid: {result.Problems[0]}{more}");
        }

        return result.Document;
    }

    // The dimensions as given: each --dimension, then DIFFRA_DIMENSION_<NAME> for each dimension the
    // dataset is stratified by and no --dimension gave. Values that cannot be kept even in a
    // rejected entry are left out, with the reason.
    private static SortedDictionary<string, string> ReadDimensions(
        Dictionary<string, List<string>> given,
        string datasetName,
        RootFileDocument? document,
        List<string> problems)
    {
        var dimensions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var text in given.GetValueOrDefault("dimension") ?? [])
        {
            var separator = text.IndexOf('=', StringComparison.Ordinal);
            if (separator < 1)
            {
                problems.Add($"--dimension '{Cut(text, 200)}' is not <name>=<value>.");
                continue;
            }

            var name = text[..separator];
            var value = text[(separator + 1)..];
            if (Length(name) > MaximumGiven)
            {
                problems.Add($"A dimension name is longer than {MaximumGiven} characters.");
            }
            else if (dimensions.ContainsKey(name))
            {
                problems.Add($"Dimension '{name}' was given more than once.");
            }
            else
            {
                dimensions[name] = CutTo(value, MaximumTrimmed, $"The value of dimension '{name}'", problems);
            }
        }

        if (document is not null && document.Datasets.TryGetValue(datasetName, out var dataset))
        {
            foreach (var name in dataset.Stratify)
            {
                var variable = "DIFFRA_DIMENSION_" + name.ToUpperInvariant();
                if (!dimensions.ContainsKey(name) && Environment(variable) is { } value)
                {
                    dimensions[name] = CutTo(value, MaximumTrimmed, $"The value of dimension '{name}'", problems);
                }
            }
        }

        return dimensions;
    }

    // Checks what was given against the root file and returns what the entry states.
    private static Source Check(
        Dictionary<string, List<string>> given,
        Context context,
        RootFileDocument? document,
        SortedDictionary<string, string> dimensions,
        List<string> problems)
    {
        foreach (var name in Once)
        {
            if (given.TryGetValue(name, out var values) && values.Count > 1)
            {
                problems.Add($"--{name} was given more than once.");
            }
        }

        var sources = Sources.Where(given.ContainsKey).ToList();
        var value = First(given, "value");
        var unit = First(given, "unit");
        var key = First(given, "key");
        var reason = First(given, "reason");
        var replaces = given.ContainsKey("replace");
        var status = given.ContainsKey("skipped") ? EntryStatus.Skipped : given.ContainsKey("failed") ? EntryStatus.Failed : EntryStatus.Recorded;

        if (sources.Count > 1)
        {
            problems.Add($"Give at most one source; {string.Join(", ", sources.Select(name => "--" + name))} were given.");
        }
        else if (sources.Count == 0 || sources[0] == "from")
        {
            problems.Add("No converter is built yet, so files cannot be read; give the number with --value.");
        }

        if (value is null)
        {
            foreach (var name in ValueOnly)
            {
                if (given.ContainsKey(name))
                {
                    problems.Add($"--{name} applies to --value only.");
                }
            }
        }

        if (status is EntryStatus.Skipped or EntryStatus.Failed)
        {
            if (string.IsNullOrEmpty(reason))
            {
                problems.Add($"--{RecordEntries.Token(status)} needs --reason <text>.");
            }
            else if (Length(reason) > MaximumText)
            {
                problems.Add($"--reason is longer than {MaximumText} characters.");
            }
        }
        else if (reason is not null)
        {
            problems.Add("--reason applies to --skipped and --failed only.");
        }

        var canonical = value is null ? null : Canonical(value, problems);
        if (unit is not null && (!IsTrimmed(unit) || Length(unit) > MaximumGiven))
        {
            problems.Add($"--unit must be 1 to {MaximumGiven} characters, with no surrounding whitespace and no control characters.");
        }

        if (key is not null && (!IsTrimmed(key) || Length(key) > MaximumTrimmed))
        {
            problems.Add($"--key must be 1 to {MaximumTrimmed} characters, with no surrounding whitespace and no control characters.");
        }

        if (document is not null)
        {
            CheckDeclared(context, document, dimensions, value is null ? null : canonical, unit, key, problems);
        }

        return new Source(status, canonical, unit, key, reason, replaces);
    }

    private static void CheckDeclared(
        Context context,
        RootFileDocument document,
        SortedDictionary<string, string> dimensions,
        string? value,
        string? unit,
        string? key,
        List<string> problems)
    {
        if (!document.Projects.TryGetValue(context.Project, out var project))
        {
            problems.Add($"The root file declares no project '{context.Project}'.");
        }

        if (!document.Datasets.TryGetValue(context.Dataset, out var dataset))
        {
            problems.Add($"The root file declares no dataset '{context.Dataset}'.");
            return;
        }

        if (project is not null && !project.Datasets.ContainsKey(dataset.Name))
        {
            problems.Add($"Project '{project.Name}' does not select dataset '{dataset.Name}'.");
        }

        foreach (var (name, given) in dimensions)
        {
            if (!document.Dimensions.TryGetValue(name, out var dimension))
            {
                problems.Add($"The root file declares no dimension '{name}'.");
            }
            else if (!dataset.Stratify.Contains(name, StringComparer.Ordinal))
            {
                problems.Add($"Dataset '{dataset.Name}' is not stratified by '{name}'.");
            }
            else if (!IsTrimmed(given))
            {
                problems.Add($"The value of dimension '{name}' must be text with no surrounding whitespace and no control characters.");
            }
            else if (dimension.Values is not null && !dimension.Values.Contains(given, StringComparer.Ordinal))
            {
                problems.Add($"Dimension '{dimension.Name}' has no value '{given}'; the root file plans for {string.Join(", ", dimension.Values)}.");
            }
        }

        foreach (var name in dataset.Stratify)
        {
            if (!dimensions.ContainsKey(name))
            {
                problems.Add($"Dataset '{dataset.Name}' is stratified by '{name}', but no value was given: give --dimension {name}=<value> or set DIFFRA_DIMENSION_{name.ToUpperInvariant()}.");
            }
        }

        if (value is null)
        {
            return;
        }

        if (Array.IndexOf(Numeric, dataset.TypeName) < 0)
        {
            problems.Add($"Dataset '{dataset.Name}' has type '{dataset.TypeName}'; --value takes a number, for a count, integer, decimal, bytes, duration or ratio dataset.");
        }
        else if (dataset.TypeName is "count" or "integer" or "bytes" && value.Contains('.', StringComparison.Ordinal))
        {
            problems.Add($"Dataset '{dataset.Name}' has type '{dataset.TypeName}', which takes whole numbers only.");
        }
        else if (dataset.TypeName is "count" or "bytes" && value.StartsWith('-'))
        {
            problems.Add($"Dataset '{dataset.Name}' has type '{dataset.TypeName}', which cannot be negative.");
        }

        if (unit is not null && dataset.Unit is not null && !string.Equals(unit, dataset.Unit, StringComparison.Ordinal))
        {
            problems.Add($"Dataset '{dataset.Name}' is measured in '{dataset.Unit}', not '{unit}'.");
        }

        if (dataset.Keyed && key is null)
        {
            problems.Add($"Dataset '{dataset.Name}' is keyed; give the member with --key.");
        }
        else if (!dataset.Keyed && key is not null)
        {
            problems.Add($"Dataset '{dataset.Name}' is not keyed, so --key does not apply.");
        }
    }

    // The canonical form of a number: no exponent, plus sign, leading zeros, trailing zeros after
    // the point or negative zero.
    internal static string? Canonical(string text, List<string> problems)
    {
        var match = NumberPattern().Match(text);
        var whole = match.Groups[2].Value;
        var fraction = match.Groups[3].Value;
        if (!match.Success || whole.Length + fraction.Length == 0)
        {
            problems.Add($"'{Cut(text, 200)}' is not a number.");
            return null;
        }

        var exponent = match.Groups[4].Success ? int.Parse(match.Groups[4].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;
        if (Math.Abs(exponent) > MaximumExponent)
        {
            problems.Add($"'{Cut(text, 200)}' has an exponent outside -{MaximumExponent} to {MaximumExponent}.");
            return null;
        }

        var digits = whole + fraction;
        var point = whole.Length + exponent;
        if (point < 0)
        {
            digits = new string('0', -point) + digits;
            point = 0;
        }
        else if (point > digits.Length)
        {
            digits += new string('0', point - digits.Length);
        }

        var integral = digits[..point].TrimStart('0');
        var fractional = digits[point..].TrimEnd('0');
        if (integral.Length == 0 && fractional.Length == 0)
        {
            return "0";
        }

        var result = (match.Groups[1].Value == "-" ? "-" : string.Empty)
            + (integral.Length == 0 ? "0" : integral)
            + (fractional.Length == 0 ? string.Empty : "." + fractional);
        if (result.Length > MaximumValueLength)
        {
            problems.Add($"The number is longer than {MaximumValueLength} characters in canonical form.");
            return null;
        }

        return result;
    }

    private static string Stated(Context context, SortedDictionary<string, string> dimensions, Source source) =>
        Write(context, dimensions, writer =>
        {
            writer.WriteString("status", RecordEntries.Token(source.Status));
            if (source.Status == EntryStatus.Recorded)
            {
                if (source.Key is not null)
                {
                    writer.WriteString("key", source.Key);
                }

                if (source.Replaces)
                {
                    writer.WriteBoolean("replaces", true);
                }

                writer.WriteStartObject("value");
                writer.WriteString("value", source.Value);
                if (source.Unit is not null)
                {
                    writer.WriteString("unit", source.Unit);
                }

                writer.WriteEndObject();
            }
            else
            {
                writer.WriteString("reason", source.Reason);
            }
        });

    private static string Conflicting(
        Context context,
        SortedDictionary<string, string> dimensions,
        Dictionary<string, List<string>> given,
        RecordEntry held)
    {
        var earlier = held.Value?.Value ?? "a different value";
        return Rejected(context, dimensions, given,
        [
            $"Job '{context.Job}' attempt {context.Attempt.ToString(CultureInfo.InvariantCulture)} already recorded {earlier} for this dataset; give --replace to replace it.",
        ]);
    }

    // A rejected entry keeps every option as given, cut to the schema's limits, with each cut stated.
    private static string Rejected(
        Context context,
        SortedDictionary<string, string> dimensions,
        Dictionary<string, List<string>> given,
        List<string> problems)
    {
        var stated = new List<string>(problems);
        var input = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, values) in given)
        {
            if (values.Count > MaximumRepeats)
            {
                stated.Add($"--{name} was given {values.Count} times; the first {MaximumRepeats} are kept.");
            }

            input[name] = values.Take(MaximumRepeats).Select(value => CutTo(value, MaximumRaw, $"--{name}", stated)).ToList();
        }

        if (stated.Count > MaximumProblems)
        {
            var dropped = stated.Count - (MaximumProblems - 1);
            stated = [.. stated.Take(MaximumProblems - 1), $"{dropped} more problems are not listed."];
        }

        return Write(context, dimensions, writer =>
        {
            writer.WriteString("status", "rejected");
            writer.WriteStartObject("input");
            foreach (var (name, values) in input)
            {
                if (values.Count == 1)
                {
                    writer.WriteString(name, values[0]);
                }
                else
                {
                    writer.WriteStartArray(name);
                    foreach (var value in values)
                    {
                        writer.WriteStringValue(value);
                    }

                    writer.WriteEndArray();
                }
            }

            writer.WriteEndObject();
            writer.WriteStartArray("problems");
            foreach (var problem in stated)
            {
                writer.WriteStringValue(Cut(problem, MaximumText));
            }

            writer.WriteEndArray();
        });
    }

    private static string Write(Context context, SortedDictionary<string, string> dimensions, Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", RecordEntries.SchemaId);
            writer.WriteString("project", context.Project);
            writer.WriteString("dataset", context.Dataset);
            writer.WriteString("commit", context.Commit);
            writer.WriteStartObject("job");
            writer.WriteString("name", context.Job);
            writer.WriteNumber("attempt", context.Attempt);
            writer.WriteEndObject();
            if (dimensions.Count > 0)
            {
                writer.WriteStartObject("dimensions");
                foreach (var (name, value) in dimensions)
                {
                    writer.WriteString(name, value);
                }

                writer.WriteEndObject();
            }

            body(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Report(RecordEntry entry, string path, bool json)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (json)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, WriterOptions))
            {
                writer.WriteStartObject();
                writer.WriteString("status", RecordEntries.Token(entry.Status));
                writer.WriteString("entriesFile", path);
                writer.WritePropertyName("entry");
                writer.WriteRawValue(entry.Json);
                writer.WriteEndObject();
            }

            Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
            return;
        }

        var what = $"{entry.Project}/{entry.Dataset}";
        Console.WriteLine(entry.Status switch
        {
            EntryStatus.Recorded => $"Recorded {what}{(entry.Key is null ? string.Empty : $" key '{entry.Key}'")} = {entry.Value!.Value}{(entry.Value.Unit is null ? string.Empty : " " + entry.Value.Unit)}.",
            EntryStatus.Rejected => $"Rejected {what}: {string.Join(" ", Problems(entry))}",
            _ => $"{(entry.Status == EntryStatus.Skipped ? "Skipped" : "Failed")} {what}.",
        });
    }

    private static IEnumerable<string> Problems(RecordEntry entry)
    {
        using var document = JsonDocument.Parse(entry.Json);
        return [.. document.RootElement.GetProperty("problems").EnumerateArray().Select(problem => problem.GetString()!)];
    }

    private static string? First(Dictionary<string, List<string>> given, string name) =>
        given.TryGetValue(name, out var values) ? values[0] : null;

    private static string? Environment(string name)
    {
        var value = System.Environment.GetEnvironmentVariable(name);
        return value is null ? null : Clean(value);
    }

    // Text with unpaired surrogates replaced, so that it can always be written as JSON.
    private static string Clean(string text)
    {
        if (!text.Any(char.IsSurrogate))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    private static bool IsTrimmed(string text) =>
        text.Length > 0
        && text.EnumerateRunes().All(rune => !Rune.IsControl(rune))
        && !Rune.IsWhiteSpace(Rune.GetRuneAt(text, 0))
        && !Rune.IsWhiteSpace(LastRune(text));

    private static Rune LastRune(string text) =>
        Rune.DecodeLastFromUtf16(text, out var rune, out _) == System.Buffers.OperationStatus.Done ? rune : Rune.ReplacementChar;

    private static int Length(string text) => text.EnumerateRunes().Count();

    private static string CutTo(string text, int limit, string what, List<string> problems)
    {
        if (Length(text) <= limit)
        {
            return text;
        }

        problems.Add($"{what} was cut to its first {limit} characters.");
        return Cut(text, limit);
    }

    private static string Cut(string text, int limit)
    {
        if (Length(text) <= limit)
        {
            return text;
        }

        var builder = new StringBuilder();
        foreach (var rune in text.EnumerateRunes().Take(limit))
        {
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }
}
