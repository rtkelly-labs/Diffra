using System.Globalization;
using System.Numerics;
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
            if (problems.Count == 0)
            {
                // Writing the entry out reads every string in it, so a lone surrogate anywhere, such
                // as in a data value that Check does not look into, is found here.
                Compact(document.RootElement);
            }

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
            TryReadAttempt(job.GetProperty("attempt"), out var attempt) ? attempt : 0,
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

        try
        {
            return JsonElement.DeepEquals(first.Data!.Value, second.Data!.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            // A number whose exponent is too large to compare: the data is the same only when it is
            // written the same way.
            return string.Equals(Compact(first.Data!.Value), Compact(second.Data!.Value), StringComparison.Ordinal);
        }
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
        else if (!TryReadAttempt(attempt, out _))
        {
            problems.Add("'job.attempt' must be a whole number from 1 to 2147483647.");
        }
    }

    /// <summary>
    /// The attempt a number stands for: a whole number from 1 to 2147483647, however it is spelt
    /// (<c>1</c>, <c>1.0</c> and <c>10e-1</c> are the same attempt). It is read from the digits as
    /// written, because a decimal would round <c>1.0000000000000000000000000000000001</c> to 1 and
    /// accept a number the schema refuses.
    /// </summary>
    private static bool TryReadAttempt(JsonElement element, out int attempt)
    {
        attempt = 0;
        if (element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        // A JSON number with a minus sign is zero or below, and never an attempt.
        var match = UnsignedNumberPattern().Match(element.GetRawText());
        if (!match.Success)
        {
            return false;
        }

        var fraction = match.Groups["fraction"].Value;
        var digits = (match.Groups["whole"].Value + fraction).TrimStart('0');
        var significant = digits.TrimEnd('0');
        var exponent = match.Groups["exponent"].Success
            ? BigInteger.Parse(match.Groups["exponent"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
            : BigInteger.Zero;

        // The value is the significant digits followed by this many zeros; fewer than none means
        // digits after the decimal point.
        var zeros = exponent - fraction.Length + (digits.Length - significant.Length);
        return significant.Length > 0
            && zeros >= 0
            && significant.Length + zeros <= 10
            && int.TryParse(significant + new string('0', (int)zeros), NumberStyles.None, CultureInfo.InvariantCulture, out attempt);
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

    /// <summary>
    /// Whether <paramref name="text"/> is non-empty text with no leading or trailing whitespace and no
    /// control characters, as the entry schema requires of names, keys, units and dimension values.
    /// </summary>
    public static bool IsTrimmed(string text) => TrimmedPattern().IsMatch(text);

    // JSON Schema counts lengths in Unicode code points, not UTF-16 units. The patterns use \z, not
    // $, and spell out ECMA-262's \s, because .NET's $ and \s differ from the schema's.
    private static int CodePoints(string text) => text.EnumerateRunes().Count();

    [GeneratedRegex(@"^[a-z][A-Za-z0-9]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^(?<whole>[0-9]+)(?:\.(?<fraction>[0-9]+))?(?:[eE](?<exponent>[+-]?[0-9]+))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex UnsignedNumberPattern();

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
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != (byte)'\n')
            {
                stream.Write("\n"u8);
            }
        }

        stream.Seek(0, SeekOrigin.End);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Appends a recorded entry unless the file already holds a different value for the same
    /// project, dataset, key and dimensions from the same job and attempt, and the entry does not
    /// replace it. On such a conflict, the entry that <paramref name="rejection"/> builds from the
    /// value already held is appended instead, and the result is <see langword="false"/>. The check
    /// and the write happen while the file is held for this process alone, so two steps that record
    /// at once cannot both pass the check. Throws <see cref="JsonException"/> when an entry to be
    /// written is not valid; the file is then left as it was.
    /// </summary>
    public static bool AppendUnlessConflicting(string path, string entryJson, Func<RecordEntry, string> rejection)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(rejection);
        var entry = RecordEntries.Parse(entryJson);
        using var stream = OpenAlone(path);
        var existing = new byte[stream.Length];
        stream.ReadExactly(existing);
        var held = entry is { Status: EntryStatus.Recorded, Replaces: false }
            ? ReadLines(existing, int.MaxValue).Entries.LastOrDefault(other =>
                other.Status == EntryStatus.Recorded
                && other.Attempt == entry.Attempt
                && string.Equals(other.JobName, entry.JobName, StringComparison.Ordinal)
                && string.Equals(other.Commit, entry.Commit, StringComparison.Ordinal)
                && string.Equals(other.Identity, entry.Identity, StringComparison.Ordinal))
            : null;
        var conflicting = held is not null && !RecordEntries.AreIdentical(held, entry);
        var written = conflicting ? RecordEntries.Parse(rejection(held!)) : entry;
        // A line left unfinished, by a writer that stopped or a hand edit, is ended first so that
        // this entry does not join it.
        var separator = existing.Length > 0 && existing[^1] != (byte)'\n' ? "\n" : string.Empty;
        stream.Seek(0, SeekOrigin.End);
        stream.Write(Encoding.UTF8.GetBytes(separator + written.Json + "\n"));
        stream.Flush(flushToDisk: true);
        return !conflicting;
    }

    private static FileStream OpenAlone(string path)
    {
        const int attempts = 200;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
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
        return ReadLines(File.ReadAllBytes(path), int.MaxValue);
    }

    /// <summary>
    /// Reads the entries in the bytes of an entries file from another job, which may be hostile:
    /// reading stops after <paramref name="maximumProblems"/> lines that are not valid entries, and
    /// <see cref="EntriesFileContent.Stopped"/> says so.
    /// </summary>
    public static EntriesFileContent Read(byte[] bytes, int maximumProblems)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumProblems);
        return ReadLines(bytes, maximumProblems);
    }

    private static EntriesFileContent ReadLines(byte[] bytes, int maximumProblems)
    {
        var entries = new List<RecordEntry>();
        var problems = new List<string>();
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
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
            if (problems.Count == maximumProblems)
            {
                return new EntriesFileContent(entries, problems) { Stopped = true };
            }

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
public sealed record EntriesFileContent(IReadOnlyList<RecordEntry> Entries, IReadOnlyList<string> Problems)
{
    /// <summary>Whether reading stopped at the limit of lines that are not valid entries.</summary>
    public bool Stopped { get; init; }
}

/// <summary>
/// A dataset the root file plans for, for one combination of the values of the dimensions whose
/// values it lists. An entry matches when it has these values, whatever its other dimensions are.
/// </summary>
public sealed record ExpectedEntry(string Project, string Dataset, IReadOnlyDictionary<string, string> Dimensions);

/// <summary>What kind of problem a merge found.</summary>
public enum MergeFindingKind
{
    /// <summary>An entry names a different commit from the one being merged.</summary>
    OtherCommit,

    /// <summary>Two entries with the same identity give different values.</summary>
    Conflict,

    /// <summary>Entries for one dataset and dimensions have different statuses, in one job or several.</summary>
    StatusConflict,

    /// <summary>A dataset was given both with <c>--value</c> and from files.</summary>
    ValueAndData,

    /// <summary>A required dataset has no entry for a dimension value the root file plans for.</summary>
    NotCollected,

    /// <summary>A job recorded a required dataset as failed for a dimension value the root file plans for.</summary>
    CollectionFailed,

    /// <summary>A job gave something for a dataset that could not be recorded.</summary>
    Rejected,
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
        var lastAttempts = new List<RecordEntry>();
        foreach (var job in forCommit.GroupBy(entry => entry.JobName, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var lastAttempt = job.Max(entry => entry.Attempt);
            foreach (var attempt in job.Select(entry => entry.Attempt).Where(attempt => attempt < lastAttempt).Distinct().Order())
            {
                superseded.Add($"{job.Key} attempt {attempt.ToString(CultureInfo.InvariantCulture)}");
            }

            lastAttempts.AddRange(job.Where(entry => entry.Attempt == lastAttempt && entry.Status != EntryStatus.Rejected));
            latest.AddRange(MergeWithinJob(job.Where(entry => entry.Attempt == lastAttempt), findings, conflicted));
        }

        var rejected = latest.Where(entry => entry.Status == EntryStatus.Rejected).ToList();
        var stated = latest.Where(entry => entry.Status != EntryStatus.Rejected).ToList();

        // Rule 4, for the whole dataset and dimensions: entries with different statuses, and a
        // --value entry against data read from files. A skipped or failed entry has no key, so
        // these are found per dataset rather than per identity. They are checked over every entry
        // of each job's last attempt, before anything is chosen or replaced within a job, so no
        // other conflict can hide them.
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in lastAttempts.GroupBy(DatasetIdentity, StringComparer.Ordinal))
        {
            var first = group.First();
            var statuses = group.GroupBy(entry => entry.Status).OrderBy(status => status.Key).ToList();
            if (statuses.Count > 1)
            {
                var parts = statuses.Select(status => $"{RecordEntries.Token(status.Key)} in {Jobs(status)}");
                findings.Add(new MergeFinding(
                    MergeFindingKind.StatusConflict,
                    $"{Describe(first.Project, first.Dataset, null, first.Dimensions)} is {string.Join("; ", parts)}."));
                blocked.Add(group.Key);
            }

            var values = group.Where(entry => entry is { Status: EntryStatus.Recorded, Value: not null }).ToList();
            var files = group.Where(entry => entry is { Status: EntryStatus.Recorded, Data: not null }).ToList();
            if (values.Count > 0 && files.Count > 0)
            {
                findings.Add(new MergeFinding(
                    MergeFindingKind.ValueAndData,
                    $"{Describe(first.Project, first.Dataset, null, first.Dimensions)} was given with --value in {Jobs(values)} and read from files in {Jobs(files)}."));
                blocked.Add(group.Key);
            }
        }

        // Rules 3 and 4: across jobs, in job name order. The entries for one identity are compared
        // as a whole, so that different values are reported within the --value entries and within
        // the data entries. An identity that conflicted within a job is still compared, so that a
        // disagreement between jobs is reported too, but no value is chosen for it.
        var chosen = new Dictionary<string, RecordEntry>(StringComparer.Ordinal);
        foreach (var group in stated.GroupBy(entry => entry.Identity, StringComparer.Ordinal))
        {
            var list = group.ToList();
            var first = list[0];
            var found = false;
            foreach (var form in new[]
            {
                list.FindAll(entry => entry is { Status: EntryStatus.Recorded, Value: not null }),
                list.FindAll(entry => entry is { Status: EntryStatus.Recorded, Data: not null }),
            })
            {
                // Entries are bucketed by a hash of what they say, so that a file of many jobs takes
                // linear time rather than comparing every pair.
                var distinct = new List<RecordEntry>();
                var buckets = new Dictionary<int, List<RecordEntry>>();
                foreach (var entry in form)
                {
                    var hash = entry.Value is { } value ? value.GetHashCode() : StructuralHash(entry.Data!.Value);
                    if (!buckets.TryGetValue(hash, out var bucket))
                    {
                        buckets[hash] = bucket = [];
                    }

                    if (!bucket.Exists(other => RecordEntries.AreIdentical(other, entry)))
                    {
                        bucket.Add(entry);
                        distinct.Add(entry);
                    }
                }

                if (distinct.Count > 1)
                {
                    findings.Add(new MergeFinding(
                        MergeFindingKind.Conflict,
                        $"{Describe(first)} has {distinct.Count.ToString(CultureInfo.InvariantCulture)} different values across {Jobs(form)}."));
                    found = true;
                }
            }

            if (found)
            {
                conflicted.Add(group.Key);
            }
            else if (!conflicted.Contains(group.Key))
            {
                chosen[group.Key] = first;
            }
        }

        // A dataset given two ways keeps nothing, after its other conflicts are reported.
        foreach (var identity in chosen.Where(pair => blocked.Contains(DatasetIdentity(pair.Value))).Select(pair => pair.Key).ToList())
        {
            chosen.Remove(identity);
        }

        // Rule 5: a required dataset with no entry for a planned dimension value. An entry that
        // conflicts was still collected; a rejected entry was not. A plan names only the dimensions
        // whose values the root file lists, so an entry matches it when it has those values,
        // whatever value it has for an observed dimension. A planned value with any entry that says
        // the collector failed is reported as a failed collection instead, because the collection
        // broke, whatever other entries say. Every entry of each job's last attempt is matched, so
        // the order of entries within a job does not matter.
        var collected = lastAttempts.ToLookup(entry => (entry.Project, entry.Dataset));
        foreach (var plan in expected)
        {
            var matching = collected[(plan.Project, plan.Dataset)].Where(entry => plan.Dimensions.All(
                    dimension => entry.Dimensions.TryGetValue(dimension.Key, out var value)
                        && string.Equals(value, dimension.Value, StringComparison.Ordinal)))
                .ToList();
            if (matching.Count == 0)
            {
                findings.Add(new MergeFinding(
                    MergeFindingKind.NotCollected,
                    $"{Describe(plan.Project, plan.Dataset, null, plan.Dimensions)} was not collected."));
            }
            else if (matching.FindAll(entry => entry.Status == EntryStatus.Failed) is { Count: > 0 } failed)
            {
                findings.Add(new MergeFinding(
                    MergeFindingKind.CollectionFailed,
                    $"{Describe(plan.Project, plan.Dataset, null, plan.Dimensions)} was recorded as failed in {Jobs(failed)}."));
            }
        }

        // A rejected entry is kept in the merged file and reported with the problems record found,
        // so that a value that could not be recorded is not lost without a word.
        foreach (var entry in rejected)
        {
            findings.Add(new MergeFinding(
                MergeFindingKind.Rejected,
                $"{Describe(entry)} was rejected in job '{entry.JobName}' attempt {entry.Attempt.ToString(CultureInfo.InvariantCulture)}: {RejectionProblems(entry)}"));
        }

        var merged = chosen.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value).ToList();
        merged.AddRange(rejected);
        var orderedFindings = findings
            .OrderBy(finding => finding.Kind)
            .ThenBy(finding => finding.Message, StringComparer.Ordinal)
            .ToList();
        return new MergeResult(merged, orderedFindings, superseded);
    }

    // A hash that agrees with JsonElement.DeepEquals: object members in any order give the same
    // hash, and every number hashes alike, since equal numbers can be written differently.
    private static int StructuralHash(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var sum = 17;
                foreach (var property in element.EnumerateObject())
                {
                    sum = unchecked(sum + HashCode.Combine(StringComparer.Ordinal.GetHashCode(property.Name), StructuralHash(property.Value)));
                }

                return sum;
            case JsonValueKind.Array:
                var combined = new HashCode();
                foreach (var item in element.EnumerateArray())
                {
                    combined.Add(StructuralHash(item));
                }

                return combined.ToHashCode();
            case JsonValueKind.String:
                return StringComparer.Ordinal.GetHashCode(element.GetString()!);
            case JsonValueKind.Number:
                return NumberHash(element.GetRawText());
            default:
                return (int)element.ValueKind;
        }
    }

    // A hash of a JSON number's exact value, so that numbers DeepEquals finds equal, such as 1, 1.0,
    // 10e-1 and 1e400 against 10e399, hash alike and different values rarely share a bucket: the
    // sign, the significant digits and the exponent, with leading and trailing zeros removed.
    private static int NumberHash(string raw)
    {
        var text = raw.AsSpan();
        var negative = text[0] == '-';
        if (negative)
        {
            text = text[1..];
        }

        var exponent = 0L;
        var e = text.IndexOfAny('e', 'E');
        if (e >= 0)
        {
            if (!long.TryParse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
            {
                // An exponent too long for a long: rare enough to share one bucket per sign.
                return HashCode.Combine(negative, "exponent");
            }

            text = text[..e];
        }

        var point = text.IndexOf('.');
        var digits = point < 0 ? text.ToString() : string.Concat(text[..point], text[(point + 1)..]);
        if (point >= 0)
        {
            exponent -= text.Length - point - 1;
        }

        digits = digits.TrimStart('0');
        if (digits.Length == 0)
        {
            return 0;
        }

        var trimmed = digits.TrimEnd('0');
        exponent += digits.Length - trimmed.Length;
        return HashCode.Combine(negative, StringComparer.Ordinal.GetHashCode(trimmed), exponent);
    }

    // The jobs, in name order and each once: "job 'a'" or "jobs 'a', 'b' and 'c'". After ten names,
    // the rest are counted rather than named.
    private static string Jobs(IEnumerable<RecordEntry> entries)
    {
        const int named = 10;
        var names = entries.Select(entry => $"'{entry.JobName}'").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (names.Count > named)
        {
            return $"jobs {string.Join(", ", names.Take(named))} and {(names.Count - named).ToString(CultureInfo.InvariantCulture)} more";
        }

        return names.Count == 1
            ? $"job {names[0]}"
            : $"jobs {string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
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
        var rejectedSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Status == EntryStatus.Rejected)
            {
                // Two rejected entries are identical exactly when their JSON is.
                if (rejectedSeen.Add(entry.Json))
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

    // The first problems a rejected entry lists, then how many more there are.
    private static string RejectionProblems(RecordEntry entry)
    {
        const int shown = 3;
        using var document = JsonDocument.Parse(entry.Json);
        var problems = document.RootElement.TryGetProperty("problems", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(problem => problem.ToString()).ToList()
            : [];
        var text = string.Join(" ", problems.Take(shown));
        return problems.Count > shown
            ? $"{text} ({(problems.Count - shown).ToString(CultureInfo.InvariantCulture)} more problems.)"
            : text;
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
