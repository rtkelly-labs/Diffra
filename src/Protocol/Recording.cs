using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Diffra.Protocol;

/// <summary>What a record entry states about its dataset. See <c>docs/record.md</c>.</summary>
public enum EntryStatus
{
    /// <summary>A value, or typed data read from files, was recorded.</summary>
    Recorded,

    /// <summary>The dataset was deliberately not collected in this job.</summary>
    Skipped,

    /// <summary>The collector broke.</summary>
    Failed,

    /// <summary>What was given could not be recorded; the entry keeps it with the problems found.</summary>
    Rejected,
}

/// <summary>A number given with <c>--value</c>, in canonical form, with its unit when one was given.</summary>
public sealed record EntryValue(string Value, string? Unit);

/// <summary>
/// One line of a job's entries file, read from JSON that validates against
/// <c>schemas/record-entry-v1.schema.json</c>. Create it with <see cref="RecordEntries.Parse"/>.
/// </summary>
public sealed class RecordEntry
{
    internal RecordEntry(
        string json,
        string project,
        string dataset,
        string commit,
        string jobName,
        int attempt,
        IReadOnlyDictionary<string, string> dimensions,
        string? key,
        EntryStatus status,
        bool replaces,
        EntryValue? value,
        JsonElement? data)
    {
        Json = json;
        Project = project;
        Dataset = dataset;
        Commit = commit;
        JobName = jobName;
        Attempt = attempt;
        Dimensions = dimensions;
        Key = key;
        Status = status;
        Replaces = replaces;
        Value = value;
        Data = data;
    }

    /// <summary>The entry as one line of compact JSON, without a line break.</summary>
    public string Json { get; }

    public string Project { get; }

    public string Dataset { get; }

    public string Commit { get; }

    public string JobName { get; }

    public int Attempt { get; }

    /// <summary>The value of each dimension, ordered by name. Empty when the entry has none.</summary>
    public IReadOnlyDictionary<string, string> Dimensions { get; }

    public string? Key { get; }

    public EntryStatus Status { get; }

    public bool Replaces { get; }

    public EntryValue? Value { get; }

    /// <summary>The typed values a converter read, when the entry was recorded from files.</summary>
    public JsonElement? Data { get; }

    /// <summary>
    /// A string that is equal for two entries exactly when their project, dataset, key and
    /// dimensions are equal. Rejected entries have one too, built from what was given.
    /// </summary>
    public string Identity => RecordEntries.IdentityOf(Project, Dataset, Key, Dimensions);
}

/// <summary>
/// Reads and validates record entries. The checks mirror
/// <c>schemas/record-entry-v1.schema.json</c>, and the tests check both against
/// <c>examples/record/</c>. The code adds two limits the schema does not state: an entry nests at
/// most <see cref="MaximumDepth"/> deep, and its text must be valid Unicode.
/// </summary>
public static partial class RecordEntries
{
    /// <summary>The schema identifier every entry carries.</summary>
    public const string SchemaId = "https://diffra.net/schemas/record-entry-v1.schema.json";

    private static readonly HashSet<string> TopLevelFields = new(StringComparer.Ordinal)
    {
        "schema", "project", "dataset", "commit", "job", "dimensions", "key", "status", "replaces",
        "value", "sources", "data", "reason", "input", "problems",
    };

    /// <summary>
    /// How deep an entry may nest. The schema sets no depth; this is a safety limit the code adds.
    /// </summary>
    public const int MaximumDepth = 256;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowDuplicateProperties = false,
        MaxDepth = MaximumDepth,
    };

    /// <summary>
    /// Every way the text fails to be a valid entry, or an empty list when it is one. Never throws
    /// on bad input.
    /// </summary>
    public static IReadOnlyList<string> Validate(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json, DocumentOptions);
            var problems = new List<string>();
            Check(document.RootElement, problems);
            return problems;
        }
        catch (JsonException exception)
        {
            return [$"The entry is not valid JSON: {exception.Message}"];
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // A lone surrogate, escaped or raw, is not text.
            return ["The entry holds text that is not valid Unicode."];
        }
    }

    /// <summary>Reads a valid entry. Throws <see cref="JsonException"/> naming every problem otherwise.</summary>
    public static RecordEntry Parse(string json)
    {
        var problems = Validate(json);
        if (problems.Count > 0)
        {
            throw new JsonException(string.Join(" ", problems));
        }

        using var document = JsonDocument.Parse(json, DocumentOptions);
        var root = document.RootElement;
        var job = root.GetProperty("job");
        var dimensions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("dimensions", out var dims))
        {
            foreach (var dimension in dims.EnumerateObject())
            {
                dimensions[dimension.Name] = dimension.Value.GetString()!;
            }
        }

        EntryValue? value = null;
        if (root.TryGetProperty("value", out var valueElement))
        {
            value = new EntryValue(
                valueElement.GetProperty("value").GetString()!,
                valueElement.TryGetProperty("unit", out var unit) ? unit.GetString() : null);
        }

        return new RecordEntry(
            Compact(root),
            root.GetProperty("project").GetString()!,
            root.GetProperty("dataset").GetString()!,
            root.GetProperty("commit").GetString()!,
            job.GetProperty("name").GetString()!,
            (int)job.GetProperty("attempt").GetDecimal(),
            dimensions,
            root.TryGetProperty("key", out var key) ? key.GetString() : null,
            ParseStatus(root.GetProperty("status").GetString()!),
            root.TryGetProperty("replaces", out _),
            value,
            root.TryGetProperty("data", out var data) ? data.Clone() : null);
    }

    /// <summary>The token an entry uses for a status.</summary>
    public static string Token(EntryStatus status) => status switch
    {
        EntryStatus.Recorded => "recorded",
        EntryStatus.Skipped => "skipped",
        EntryStatus.Failed => "failed",
        EntryStatus.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown entry status."),
    };

    /// <summary>Whether two entries with the same identity say the same thing.</summary>
    public static bool AreIdentical(RecordEntry first, RecordEntry second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.Status != second.Status || first.Identity != second.Identity)
        {
            return false;
        }

        if (first.Status == EntryStatus.Rejected)
        {
            return string.Equals(first.Json, second.Json, StringComparison.Ordinal);
        }

        if (first.Status != EntryStatus.Recorded)
        {
            return true;
        }

        if (first.Value is not null || second.Value is not null)
        {
            return first.Value == second.Value;
        }

        return JsonElement.DeepEquals(first.Data!.Value, second.Data!.Value);
    }

    internal static string IdentityOf(
        string project,
        string dataset,
        string? key,
        IReadOnlyDictionary<string, string> dimensions)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(project);
            writer.WriteStringValue(dataset);
            if (key is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(key);
            }

            writer.WriteStartArray();
            foreach (var dimension in dimensions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteStringValue(dimension.Key);
                writer.WriteStringValue(dimension.Value);
            }

            writer.WriteEndArray();
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static EntryStatus ParseStatus(string token) => token switch
    {
        "recorded" => EntryStatus.Recorded,
        "skipped" => EntryStatus.Skipped,
        "failed" => EntryStatus.Failed,
        _ => EntryStatus.Rejected,
    };

    private static string Compact(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Check(JsonElement root, List<string> problems)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            problems.Add("An entry must be a JSON object.");
            return;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (!TopLevelFields.Contains(property.Name))
            {
                problems.Add($"'{property.Name}' is not an entry field.");
            }
        }

        foreach (var field in new[] { "schema", "project", "dataset", "commit", "job", "status" })
        {
            if (!root.TryGetProperty(field, out _))
            {
                problems.Add($"'{field}' is required.");
            }
        }

        var status = root.TryGetProperty("status", out var statusElement) ? statusElement.ToString() : null;
        if (status is not null && (statusElement.ValueKind != JsonValueKind.String
            || status is not ("recorded" or "skipped" or "failed" or "rejected")))
        {
            problems.Add("'status' must be recorded, skipped, failed or rejected.");
            status = null;
        }

        var rejected = status == "rejected";

        if (root.TryGetProperty("schema", out var schema)
            && (schema.ValueKind != JsonValueKind.String || schema.GetString() != SchemaId))
        {
            problems.Add($"'schema' must be {SchemaId}.");
        }

        foreach (var field in new[] { "project", "dataset" })
        {
            if (root.TryGetProperty(field, out var element))
            {
                CheckGiven(element, field, problems);
                if (!rejected && element.ValueKind == JsonValueKind.String && !NamePattern().IsMatch(element.GetString()!))
                {
                    problems.Add($"'{field}' must be a declared name, such as 'main' or 'packageSize'.");
                }
            }
        }

        if (root.TryGetProperty("commit", out var commit)
            && (commit.ValueKind != JsonValueKind.String || !CommitPattern().IsMatch(commit.GetString()!)))
        {
            problems.Add("'commit' must be a full commit ID: 40 or 64 lower-case hexadecimal characters.");
        }

        if (root.TryGetProperty("job", out var job))
        {
            CheckJob(job, problems);
        }

        if (root.TryGetProperty("dimensions", out var dimensions))
        {
            CheckDimensions(dimensions, rejected, problems);
        }

        if (root.TryGetProperty("key", out var key))
        {
            CheckTrimmed(key, "key", 1000, problems);
        }

        if (root.TryGetProperty("replaces", out var replaces) && replaces.ValueKind != JsonValueKind.True)
        {
            problems.Add("'replaces' must be true when present.");
        }

        if (root.TryGetProperty("value", out var value))
        {
            CheckValue(value, problems);
        }

        if (root.TryGetProperty("sources", out var sources))
        {
            CheckSources(sources, problems);
        }

        if (root.TryGetProperty("data", out var data)
            && data.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            problems.Add("'data' must be an object or an array.");
        }

        if (root.TryGetProperty("reason", out var reason))
        {
            CheckText(reason, "reason", problems);
        }

        if (root.TryGetProperty("input", out var input))
        {
            CheckInput(input, problems);
        }

        if (root.TryGetProperty("problems", out var problemList))
        {
            CheckProblems(problemList, problems);
        }

        if (status is not null)
        {
            CheckStatusRules(root, status, problems);
        }
    }

    private static void CheckStatusRules(JsonElement root, string status, List<string> problems)
    {
        bool Has(string field) => root.TryGetProperty(field, out _);

        void Forbid(params string[] fields)
        {
            foreach (var field in fields)
            {
                if (Has(field))
                {
                    problems.Add($"A {status} entry must not have '{field}'.");
                }
            }
        }

        void Require(params string[] fields)
        {
            foreach (var field in fields)
            {
                if (!Has(field))
                {
                    problems.Add($"A {status} entry must have '{field}'.");
                }
            }
        }

        switch (status)
        {
            case "rejected":
                Require("input", "problems");
                Forbid("value", "data", "reason", "replaces");
                break;
            case "recorded":
                Forbid("input", "problems", "reason");
                var fromValue = Has("value");
                var fromFiles = Has("sources") || Has("data");
                if (fromValue == fromFiles)
                {
                    problems.Add("A recorded entry must have either 'value', or both 'sources' and 'data'.");
                }
                else if (fromFiles)
                {
                    Require("sources", "data");
                    Forbid("key");
                }

                break;
            default:
                Forbid("input", "problems", "value", "sources", "data", "replaces", "key");
                Require("reason");
                break;
        }
    }

    private static void CheckJob(JsonElement job, List<string> problems)
    {
        if (job.ValueKind != JsonValueKind.Object)
        {
            problems.Add("'job' must be an object with 'name' and 'attempt'.");
            return;
        }

        foreach (var property in job.EnumerateObject())
        {
            if (property.Name is not ("name" or "attempt"))
            {
                problems.Add($"'job.{property.Name}' is not a job field.");
            }
        }

        if (job.TryGetProperty("name", out var name))
        {
            CheckTrimmed(name, "job.name", 200, problems);
        }
        else
        {
            problems.Add("'job.name' is required.");
        }

        if (!job.TryGetProperty("attempt", out var attempt))
        {
            problems.Add("'job.attempt' is required.");
        }
        else if (attempt.ValueKind != JsonValueKind.Number
            || !attempt.TryGetDecimal(out var number)
            || number < 1
            || number > int.MaxValue
            || number != decimal.Truncate(number))
        {
            problems.Add("'job.attempt' must be a whole number from 1 to 2147483647.");
        }
    }

    private static void CheckDimensions(JsonElement dimensions, bool rejected, List<string> problems)
    {
        if (dimensions.ValueKind != JsonValueKind.Object)
        {
            problems.Add("'dimensions' must be an object.");
            return;
        }

        var count = 0;
        foreach (var dimension in dimensions.EnumerateObject())
        {
            count++;
            var path = $"dimensions.{dimension.Name}";
            if (dimension.Name.Length == 0 || CodePoints(dimension.Name) > 200)
            {
                problems.Add($"The dimension name '{dimension.Name}' must be 1 to 200 characters.");
            }
            else if (!rejected && !NamePattern().IsMatch(dimension.Name))
            {
                problems.Add($"The dimension name '{dimension.Name}' must be a declared name.");
            }

            if (rejected)
            {
                if (dimension.Value.ValueKind != JsonValueKind.String || CodePoints(dimension.Value.GetString()!) > 1000)
                {
                    problems.Add($"'{path}' must be text of at most 1000 characters.");
                }
            }
            else
            {
                CheckTrimmed(dimension.Value, path, 1000, problems);
            }
        }

        if (count == 0)
        {
            problems.Add("'dimensions' is omitted when there are none, never empty.");
        }
    }

    private static void CheckValue(JsonElement value, List<string> problems)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            problems.Add("'value' must be an object with 'value' and an optional 'unit'.");
            return;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (property.Name is not ("value" or "unit"))
            {
                problems.Add($"'value.{property.Name}' is not a value field.");
            }
        }

        if (!value.TryGetProperty("value", out var number))
        {
            problems.Add("'value.value' is required.");
        }
        else if (number.ValueKind != JsonValueKind.String
            || CodePoints(number.GetString()!) > 100
            || !CanonicalNumberPattern().IsMatch(number.GetString()!))
        {
            problems.Add("'value.value' must be a number in canonical form, such as '12.5'.");
        }

        if (value.TryGetProperty("unit", out var unit))
        {
            CheckTrimmed(unit, "value.unit", 200, problems);
        }
    }

    private static void CheckSources(JsonElement sources, List<string> problems)
    {
        if (sources.ValueKind != JsonValueKind.Array)
        {
            problems.Add("'sources' must be a list.");
            return;
        }

        var length = sources.GetArrayLength();
        if (length is < 1 or > 1000)
        {
            problems.Add("'sources' must list 1 to 1000 files.");
        }

        var seen = new List<JsonElement>();
        var index = 0;
        foreach (var source in sources.EnumerateArray())
        {
            var path = $"sources[{index.ToString(CultureInfo.InvariantCulture)}]";
            index++;
            if (seen.Any(earlier => JsonElement.DeepEquals(earlier, source)))
            {
                problems.Add($"'{path}' repeats an earlier source.");
            }

            seen.Add(source);
            CheckSource(source, path, problems);
        }
    }

    private static void CheckSource(JsonElement source, string path, List<string> problems)
    {
        if (source.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"'{path}' must be an object with 'path', 'format' and 'sha256'.");
            return;
        }

        foreach (var property in source.EnumerateObject())
        {
            if (property.Name is not ("path" or "format" or "sha256"))
            {
                problems.Add($"'{path}.{property.Name}' is not a source field.");
            }
        }

        if (!source.TryGetProperty("path", out var file))
        {
            problems.Add($"'{path}.path' is required.");
        }
        else if (file.ValueKind != JsonValueKind.String || !IsSafeSourcePath(file.GetString()!))
        {
            problems.Add($"'{path}.path' must be relative to the repository root, with single forward slashes and no '.' or '..' segments.");
        }

        if (!source.TryGetProperty("format", out var format))
        {
            problems.Add($"'{path}.format' is required.");
        }
        else if (format.ValueKind != JsonValueKind.String
            || format.GetString()!.Length > 64
            || !OptionPattern().IsMatch(format.GetString()!))
        {
            problems.Add($"'{path}.format' must name a converter, such as 'junit'.");
        }

        if (!source.TryGetProperty("sha256", out var digest))
        {
            problems.Add($"'{path}.sha256' is required.");
        }
        else if (digest.ValueKind != JsonValueKind.String || !DigestPattern().IsMatch(digest.GetString()!))
        {
            problems.Add($"'{path}.sha256' must be 64 lower-case hexadecimal characters.");
        }
    }

    /// <summary>
    /// Whether a source path is relative to the repository root, uses single forward slashes, and
    /// has no '.' or '..' segment, no leading '/' or '~', no trailing '/', no backslash and no
    /// control characters.
    /// </summary>
    public static bool IsSafeSourcePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return CodePoints(path) is >= 1 and <= 1000
            && SourcePathPattern().IsMatch(path)
            && !UnsafeSegmentPattern().IsMatch(path);
    }

    private static void CheckInput(JsonElement input, List<string> problems)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            problems.Add("'input' must be an object of options by name.");
            return;
        }

        var count = 0;
        foreach (var option in input.EnumerateObject())
        {
            count++;
            if (option.Name.Length > 64 || !OptionPattern().IsMatch(option.Name))
            {
                problems.Add($"'input' names an option '{option.Name}' that is not a lower-case option name.");
            }

            var value = option.Value;
            if (value.ValueKind == JsonValueKind.Array)
            {
                var length = value.GetArrayLength();
                if (length is < 2 or > 100 || value.EnumerateArray().Any(item => !IsRaw(item)))
                {
                    problems.Add($"'input.{option.Name}' must be 2 to 100 texts of at most 2000 characters.");
                }
            }
            else if (!IsRaw(value))
            {
                problems.Add($"'input.{option.Name}' must be text of at most 2000 characters.");
            }
        }

        if (count is < 1 or > 50)
        {
            problems.Add("'input' must hold 1 to 50 options.");
        }
    }

    private static void CheckProblems(JsonElement list, List<string> problems)
    {
        if (list.ValueKind != JsonValueKind.Array)
        {
            problems.Add("'problems' must be a list.");
            return;
        }

        var length = list.GetArrayLength();
        if (length is < 1 or > 50)
        {
            problems.Add("'problems' must hold 1 to 50 texts.");
        }

        foreach (var item in list.EnumerateArray())
        {
            CheckText(item, "problems[]", problems);
        }
    }

    private static bool IsRaw(JsonElement element) =>
        element.ValueKind == JsonValueKind.String && CodePoints(element.GetString()!) <= 2000;

    private static void CheckGiven(JsonElement element, string path, List<string> problems)
    {
        if (element.ValueKind != JsonValueKind.String || CodePoints(element.GetString()!) is < 1 or > 200)
        {
            problems.Add($"'{path}' must be text of 1 to 200 characters.");
        }
    }

    private static void CheckText(JsonElement element, string path, List<string> problems)
    {
        if (element.ValueKind != JsonValueKind.String || CodePoints(element.GetString()!) is < 1 or > 2000)
        {
            problems.Add($"'{path}' must be text of 1 to 2000 characters.");
        }
    }

    private static void CheckTrimmed(JsonElement element, string path, int maxLength, List<string> problems)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            problems.Add($"'{path}' must be text.");
            return;
        }

        var text = element.GetString()!;
        if (CodePoints(text) is var length && (length < 1 || length > maxLength))
        {
            problems.Add($"'{path}' must be 1 to {maxLength.ToString(CultureInfo.InvariantCulture)} characters.");
        }
        else if (!TrimmedPattern().IsMatch(text))
        {
            problems.Add($"'{path}' must have no leading or trailing whitespace and no control characters.");
        }
    }

    // JSON Schema counts lengths in Unicode code points, not UTF-16 units. The patterns use \z, not
    // $, and spell out ECMA-262's \s, because .NET's $ and \s differ from the schema's.
    private static int CodePoints(string text) => text.EnumerateRunes().Count();

    [GeneratedRegex(@"^[a-z][A-Za-z0-9]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^([0-9a-f]{40}|[0-9a-f]{64})\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    [GeneratedRegex(@"^[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex OptionPattern();

    [GeneratedRegex(@"^(0|-?[1-9][0-9]*(\.[0-9]*[1-9])?|-?0\.[0-9]*[1-9])\z", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalNumberPattern();

    [GeneratedRegex(@"^[^\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff\x00-\x1f\x7f]([^\x00-\x1f\x7f]*[^\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff\x00-\x1f\x7f])?\z", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex TrimmedPattern();

    [GeneratedRegex(@"^[^/\\~\x00-\x1f\x7f][^\\\x00-\x1f\x7f]*\z", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex SourcePathPattern();

    [GeneratedRegex(@"(^|/)\.{1,2}(/|\z)|//|/\z", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeSegmentPattern();
}

/// <summary>
/// A job's entries file: append-only JSON lines in <c>&lt;run-dir&gt;/entries/</c>, one file per job
/// and attempt.
/// </summary>
public static partial class EntriesFile
{
    private const int MaximumStemLength = 64;

    /// <summary>
    /// The file name for a job and attempt: the job name made safe for a file name, the attempt,
    /// and a short digest of both, so that two names that sanitise alike do not collide. The name
    /// carries no meaning; the entries say which job and attempt wrote them.
    /// </summary>
    public static string FileName(string jobName, int attempt)
    {
        ArgumentNullException.ThrowIfNull(jobName);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var attemptText = attempt.ToString(CultureInfo.InvariantCulture);
        var stem = UnsafeFileCharacters().Replace(jobName, "-").Trim('-', '.');
        if (stem.Length > MaximumStemLength)
        {
            stem = stem[..MaximumStemLength].TrimEnd('-', '.');
        }

        if (stem.Length == 0)
        {
            stem = "job";
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(jobName + "\0" + attemptText));
        var shortDigest = Convert.ToHexString(digest, 0, 6).ToLowerInvariant();
        return $"{stem}.{attemptText}.{shortDigest}.jsonl";
    }

    /// <summary>
    /// Appends one entry as a single line. The file is opened for this process alone while the line
    /// is written, so two steps of one job that record at once wait for each other rather than
    /// overwrite each other's line. Throws <see cref="JsonException"/> when the entry is not valid;
    /// the file is then left as it was.
    /// </summary>
    public static void Append(string path, string entryJson)
    {
        ArgumentNullException.ThrowIfNull(path);
        var entry = RecordEntries.Parse(entryJson);
        var bytes = Encoding.UTF8.GetBytes(entry.Json + "\n");
        using var stream = OpenAlone(path);
        stream.Seek(0, SeekOrigin.End);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static FileStream OpenAlone(string path)
    {
        const int attempts = 200;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (attempt < attempts && File.Exists(path))
            {
                // Another writer holds the file; wait for it to finish its line.
                Thread.Sleep(10);
            }
        }
    }

    /// <summary>
    /// Reads every entry in an entries file. A line that is not a valid entry is reported with its
    /// line number and left out; reading never stops at the first problem.
    /// </summary>
    public static EntriesFileContent Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var entries = new List<RecordEntry>();
        var problems = new List<string>();
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var bytes = File.ReadAllBytes(path);
        var number = 0;
        // A byte-order mark written by another tool is skipped, as File.ReadLines would.
        var start = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
        while (start < bytes.Length)
        {
            var end = Array.IndexOf(bytes, (byte)'\n', start);
            if (end < 0)
            {
                end = bytes.Length;
            }

            number++;
            var span = bytes.AsSpan(start, end - start);
            start = end + 1;
            if (span.IsEmpty)
            {
                continue;
            }

            string line;
            try
            {
                line = strict.GetString(span);
            }
            catch (DecoderFallbackException)
            {
                problems.Add($"Line {number.ToString(CultureInfo.InvariantCulture)}: The line is not valid UTF-8.");
                continue;
            }

            var lineProblems = RecordEntries.Validate(line);
            if (lineProblems.Count > 0)
            {
                problems.Add($"Line {number.ToString(CultureInfo.InvariantCulture)}: {string.Join(" ", lineProblems)}");
                continue;
            }

            entries.Add(RecordEntries.Parse(line));
        }

        return new EntriesFileContent(entries, problems);
    }

    [GeneratedRegex("[^A-Za-z0-9._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeFileCharacters();
}

/// <summary>The valid entries of an entries file, in file order, and the lines that were not valid.</summary>
public sealed record EntriesFileContent(IReadOnlyList<RecordEntry> Entries, IReadOnlyList<string> Problems);

/// <summary>A dataset the root file plans for, for one combination of dimension values.</summary>
public sealed record ExpectedEntry(string Project, string Dataset, IReadOnlyDictionary<string, string> Dimensions);

/// <summary>What kind of problem a merge found.</summary>
public enum MergeFindingKind
{
    /// <summary>An entry names a different commit from the one being merged.</summary>
    OtherCommit,

    /// <summary>Two entries with the same identity give different values.</summary>
    Conflict,

    /// <summary>One job recorded a dataset that another job skipped or failed.</summary>
    StatusConflict,

    /// <summary>A dataset was given both with <c>--value</c> and from files.</summary>
    ValueAndData,

    /// <summary>A required dataset has no entry for a dimension value the root file plans for.</summary>
    NotCollected,
}

/// <summary>One problem a merge found.</summary>
public sealed record MergeFinding(MergeFindingKind Kind, string Message);

/// <summary>The result of merging the jobs' entries for one commit.</summary>
public sealed record MergeResult(
    IReadOnlyList<RecordEntry> Entries,
    IReadOnlyList<MergeFinding> Findings,
    IReadOnlyList<string> SupersededAttempts);

/// <summary>
/// Merges the entries of every job for one commit, following the proposed rules in
/// <c>docs/record.md</c>.
/// </summary>
public static class EntryMerge
{
    /// <summary>
    /// Merges <paramref name="entries"/> for <paramref name="commit"/>. The result's entries are
    /// ordered by identity, then rejected entries by job name. Values that conflict, whether within
    /// one job or between jobs, are left out and reported; neither is chosen.
    /// </summary>
    public static MergeResult Merge(string commit, IEnumerable<RecordEntry> entries, IEnumerable<ExpectedEntry> expected)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(expected);
        var findings = new List<MergeFinding>();

        var forCommit = new List<RecordEntry>();
        foreach (var entry in entries)
        {
            if (string.Equals(entry.Commit, commit, StringComparison.Ordinal))
            {
                forCommit.Add(entry);
            }
            else
            {
                findings.Add(new MergeFinding(
                    MergeFindingKind.OtherCommit,
                    $"Job '{entry.JobName}' recorded {Describe(entry)} for commit {entry.Commit}, not {commit}."));
            }
        }

        // Rule 1: a later attempt of the same job replaces every entry of the earlier attempt.
        // Rule 2: within the attempt that is kept, --replace replaces an earlier recorded entry.
        var superseded = new List<string>();
        var conflicted = new HashSet<string>(StringComparer.Ordinal);
        var latest = new List<RecordEntry>();
        foreach (var job in forCommit.GroupBy(entry => entry.JobName, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var lastAttempt = job.Max(entry => entry.Attempt);
            foreach (var attempt in job.Select(entry => entry.Attempt).Where(attempt => attempt < lastAttempt).Distinct().Order())
            {
                superseded.Add($"{job.Key} attempt {attempt.ToString(CultureInfo.InvariantCulture)}");
            }

            latest.AddRange(MergeWithinJob(job.Where(entry => entry.Attempt == lastAttempt), findings, conflicted));
        }

        var rejected = latest.Where(entry => entry.Status == EntryStatus.Rejected).ToList();
        var stated = latest.Where(entry => entry.Status != EntryStatus.Rejected).ToList();

        // Rule 4: a --value entry against data read from files for the same dataset and dimensions.
        // Checked before anything is chosen, so a conflict elsewhere cannot hide it.
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in stated
            .Where(entry => entry.Status == EntryStatus.Recorded)
            .GroupBy(DatasetIdentity, StringComparer.Ordinal))
        {
            var value = group.FirstOrDefault(entry => entry.Value is not null);
            var files = group.FirstOrDefault(entry => entry.Data is not null);
            if (value is not null && files is not null)
            {
                findings.Add(new MergeFinding(
                    MergeFindingKind.ValueAndData,
                    $"{Describe(files)} was given with --value in job '{value.JobName}' and read from files in job '{files.JobName}'."));
                blocked.Add(group.Key);
            }
        }

        // Rules 3 and 4: across jobs, in job name order. The entries for one identity are compared
        // as a whole, so that each kind of disagreement is reported: a status against another
        // status, and different values within the --value entries and within the data entries.
        var chosen = new Dictionary<string, RecordEntry>(StringComparer.Ordinal);
        foreach (var group in stated.GroupBy(entry => entry.Identity, StringComparer.Ordinal))
        {
            if (conflicted.Contains(group.Key))
            {
                continue;
            }

            var list = group.ToList();
            var first = list[0];
            var found = false;
            var otherStatus = list.Find(entry => entry.Status != first.Status);
            if (otherStatus is not null)
            {
                findings.Add(new MergeFinding(
                    MergeFindingKind.StatusConflict,
                    $"{Describe(first)} is {RecordEntries.Token(first.Status)} in job '{first.JobName}' and {RecordEntries.Token(otherStatus.Status)} in job '{otherStatus.JobName}'."));
                found = true;
            }

            foreach (var form in new[]
            {
                list.FindAll(entry => entry.Status == EntryStatus.Recorded && entry.Value is not null),
                list.FindAll(entry => entry.Status == EntryStatus.Recorded && entry.Data is not null),
            })
            {
                var differing = form.Skip(1).FirstOrDefault(entry => !RecordEntries.AreIdentical(form[0], entry));
                if (differing is not null)
                {
                    findings.Add(new MergeFinding(
                        MergeFindingKind.Conflict,
                        $"{Describe(first)} has different values in jobs '{form[0].JobName}' and '{differing.JobName}'."));
                    found = true;
                }
            }

            if (found)
            {
                conflicted.Add(group.Key);
            }
            else
            {
                chosen[group.Key] = first;
            }
        }

        // A dataset given both ways keeps neither, after its other conflicts are reported.
        foreach (var identity in chosen.Where(pair => blocked.Contains(DatasetIdentity(pair.Value))).Select(pair => pair.Key).ToList())
        {
            chosen.Remove(identity);
        }

        // Rule 5: a required dataset with no entry for a planned dimension value. An entry that
        // conflicts was still collected; a rejected entry was not.
        var collected = new HashSet<string>(stated.Select(DatasetIdentity), StringComparer.Ordinal);
        foreach (var plan in expected)
        {
            if (!collected.Contains(RecordEntries.IdentityOf(plan.Project, plan.Dataset, null, plan.Dimensions)))
            {
                findings.Add(new MergeFinding(
                    MergeFindingKind.NotCollected,
                    $"{Describe(plan.Project, plan.Dataset, null, plan.Dimensions)} was not collected."));
            }
        }

        var merged = chosen.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value).ToList();
        merged.AddRange(rejected);
        var orderedFindings = findings
            .OrderBy(finding => finding.Kind)
            .ThenBy(finding => finding.Message, StringComparer.Ordinal)
            .ToList();
        return new MergeResult(merged, orderedFindings, superseded);
    }

    private static string DatasetIdentity(RecordEntry entry) =>
        RecordEntries.IdentityOf(entry.Project, entry.Dataset, null, entry.Dimensions);

    // Rule 2, and identical repeats, within one job and attempt, in file order. Two different
    // values without --replace can only come from a hand-edited file, since `record` refuses
    // them; the identity is marked conflicted and no value is chosen.
    private static List<RecordEntry> MergeWithinJob(
        IEnumerable<RecordEntry> entries,
        List<MergeFinding> findings,
        HashSet<string> conflicted)
    {
        var result = new List<RecordEntry>();
        var byIdentity = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Status == EntryStatus.Rejected)
            {
                if (!result.Any(earlier => RecordEntries.AreIdentical(earlier, entry)))
                {
                    result.Add(entry);
                }

                continue;
            }

            if (!byIdentity.TryGetValue(entry.Identity, out var index))
            {
                byIdentity[entry.Identity] = result.Count;
                result.Add(entry);
                continue;
            }

            var earlier = result[index];
            if (entry.Replaces && earlier.Status == EntryStatus.Recorded && entry.Status == EntryStatus.Recorded)
            {
                result[index] = entry;
            }
            else if (!RecordEntries.AreIdentical(earlier, entry) && conflicted.Add(entry.Identity))
            {
                findings.Add(new MergeFinding(
                    MergeFindingKind.Conflict,
                    $"{Describe(entry)} has different values within job '{entry.JobName}' attempt {entry.Attempt.ToString(CultureInfo.InvariantCulture)}."));
            }
        }

        return result;
    }

    private static string Describe(RecordEntry entry) =>
        Describe(entry.Project, entry.Dataset, entry.Key, entry.Dimensions);

    private static string Describe(
        string project,
        string dataset,
        string? key,
        IReadOnlyDictionary<string, string> dimensions)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{project}/{dataset}");
        if (key is not null)
        {
            builder.Append(CultureInfo.InvariantCulture, $" key '{key}'");
        }

        if (dimensions.Count > 0)
        {
            builder.Append(" (");
            builder.Append(string.Join(", ", dimensions
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}")));
            builder.Append(')');
        }

        return builder.ToString();
    }
}
