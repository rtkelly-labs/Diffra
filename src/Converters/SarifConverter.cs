using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Diffra.Converters;

/// <summary>
/// The <c>sarif</c> converter for <c>diffra/findings@1</c> (docs/datasets.md): every result in
/// the SARIF 2.1.0 logs a glob matches, keyed by tool, rule, path and fingerprint, with a count per
/// tool and rule and a total. It reads JSON only within the safety limits for untrusted input, and
/// never follows a URI it reads.
/// </summary>
public static class SarifConverter
{
    /// <summary>The converter's name under <c>from</c> and in each source's <c>format</c>.</summary>
    public const string Name = "sarif";

    /// <summary>The dataset type it reads.</summary>
    public const string DatasetType = "diffra/findings@1";

    /// <summary>The one SARIF version it reads.</summary>
    public const string Version = "2.1.0";

    /// <summary>The most bytes one file may hold: 64 MiB.</summary>
    public const long MaximumFileBytes = 64L * 1024 * 1024;

    /// <summary>The most bytes the files of one entry may hold together: 256 MiB.</summary>
    public const long MaximumEntryBytes = 256L * 1024 * 1024;

    /// <summary>How deep a SARIF file may nest.</summary>
    public const int MaximumDepth = 256;

    /// <summary>The path of a result that has no location.</summary>
    public const string NoPath = "<none>";

    /// <summary>
    /// The most characters the messages filled from their arguments may hold together in one
    /// entry, so that a short message string repeating a long argument cannot fill memory.
    /// </summary>
    public const long MaximumFilledCharacters = 64L * 1024 * 1024;

    // The most problems one file adds, so that one broken file cannot hide the others' problems.
    private const int MaximumProblemsPerFile = 10;

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaximumDepth + 1,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaximumDepth,
    };

    private static readonly string[] Levels = ["error", "warning", "note", "none"];

    /// <summary>
    /// Reads the SARIF files <paramref name="glob"/> matches under <paramref name="root"/>. More
    /// findings by key than <paramref name="maximumKeys"/>, the root file's <c>maxKeys</c> budget,
    /// rejects the entry, because the entry schema cannot yet state which keys a budget dropped. A
    /// message longer than <paramref name="maximumTextLength"/> is cut and ends with <c>…</c>.
    /// </summary>
    public static ConverterResult Read(string root, string glob, int? maximumKeys, int? maximumTextLength) =>
        Read(root, glob, maximumKeys, maximumTextLength, MaximumFileBytes, MaximumEntryBytes);

    internal static ConverterResult Read(string root, string glob, int? maximumKeys, int? maximumTextLength, long fileLimit, long entryLimit, long fillLimit = MaximumFilledCharacters)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(glob);
        var problems = new List<string>();
        var match = SourceFiles.Find(root, glob, problems);
        if (match is null || problems.Count > 0)
        {
            return new ConverterResult([], null, problems);
        }

        var sources = new List<ConverterSource>(match.Files.Count);
        var findings = new Findings();
        var fill = new FillBudget(fillLimit);
        long total = 0;
        foreach (var path in match.Files)
        {
            byte[] bytes;
            try
            {
                using var stream = SourceFiles.OpenRegular(Path.Combine(root, path));
                var length = stream.Length;
                if (length > fileLimit)
                {
                    problems.Add($"'{path}' is {Bytes(length)}, more than the {Bytes(fileLimit)} one file may hold; it was not read.");
                    break;
                }

                if (total + length > entryLimit)
                {
                    problems.Add($"The files '{glob}' matches hold more than the {Bytes(entryLimit)} one entry may read; '{path}' and the files after it were not read.");
                    break;
                }

                total += length;
                bytes = SourceFiles.ReadAll(stream, length);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                problems.Add($"'{path}' cannot be read: {exception.Message}");
                break;
            }

            sources.Add(new ConverterSource(path, Name, Convert.ToHexStringLower(SHA256.HashData(bytes))));
            var fileProblems = new List<string>();
            new LogReader(path, root, maximumTextLength, findings, fill, fileProblems).ReadFile(bytes);
            if (fileProblems.Count > MaximumProblemsPerFile)
            {
                var more = fileProblems.Count - MaximumProblemsPerFile;
                fileProblems = [.. fileProblems.Take(MaximumProblemsPerFile), $"'{path}' has {more.ToString(CultureInfo.InvariantCulture)} more problems that are not listed."];
            }

            problems.AddRange(fileProblems);
        }

        if (problems.Count == 0)
        {
            findings.Check(problems);
        }

        if (problems.Count == 0 && maximumKeys is { } budget && findings.Keys > budget)
        {
            problems.Add($"The files '{glob}' matches hold {findings.Keys.ToString(CultureInfo.InvariantCulture)} findings by key, more than the root file's maxKeys budget of {budget.ToString(CultureInfo.InvariantCulture)}. "
                + "Keeping only the errors and warnings needs the entry to state what was dropped, which the entry schema cannot do yet; narrow the glob or raise maxKeys.");
        }

        return problems.Count > 0 ? new ConverterResult(sources, null, problems) : new ConverterResult(sources, findings.Write(), []);
    }

    // A byte count as a problem states it: whole MiB when it is a whole number of them.
    private static string Bytes(long bytes) =>
        bytes % (1024 * 1024) == 0
            ? $"{(bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MiB"
            : $"{bytes.ToString(CultureInfo.InvariantCulture)} bytes";

    /// <summary>The severity order of a level: error first, then warning, note and none.</summary>
    private static int Severity(string level) => Array.IndexOf(Levels, level);

    // One finding as it is recorded. Line is null when the result gives no start line.
    private sealed record Finding(string Tool, string Rule, string Path, string Fingerprint, string Level, long? Line, string Message)
    {
        public string Key => $"{Tool}/{Rule}/{Path}/{Fingerprint}";
    }

    // Orders the findings that share a key, so that the one kept does not depend on input order:
    // the most severe level, then the lowest line (a line before none), then the message.
    private sealed class Precedence : IComparer<Finding>
    {
        public static readonly Precedence Instance = new();

        public int Compare(Finding? x, Finding? y)
        {
            ArgumentNullException.ThrowIfNull(x);
            ArgumentNullException.ThrowIfNull(y);
            var order = Severity(x.Level).CompareTo(Severity(y.Level));
            if (order != 0)
            {
                return order;
            }

            order = (x.Line ?? long.MaxValue).CompareTo(y.Line ?? long.MaxValue);
            if (order != 0)
            {
                return order;
            }

            order = (x.Line is null).CompareTo(y.Line is null);
            return order != 0 ? order : string.CompareOrdinal(x.Message, y.Message);
        }
    }

    // Every finding read from an entry's files.
    private sealed class Findings
    {
        private readonly SortedDictionary<string, (Finding Kept, int Occurrences)> results = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, int> byRule = new(StringComparer.Ordinal);
        private readonly List<string> collisions = [];
        private int total;

        public int Keys => results.Count;

        public void Add(Finding finding)
        {
            total++;
            var rule = $"{finding.Tool}/{finding.Rule}";
            byRule[rule] = byRule.GetValueOrDefault(rule) + 1;
            if (!results.TryGetValue(finding.Key, out var held))
            {
                results[finding.Key] = (finding, 1);
                return;
            }

            if (held.Kept.Tool != finding.Tool || held.Kept.Rule != finding.Rule || held.Kept.Path != finding.Path || held.Kept.Fingerprint != finding.Fingerprint)
            {
                collisions.Add(finding.Key);
            }

            var kept = Precedence.Instance.Compare(finding, held.Kept) < 0 ? finding : held.Kept;
            results[finding.Key] = (kept, held.Occurrences + 1);
        }

        // Two different findings whose parts join to the same key, which could only be told apart
        // by a key the spec does not define, reject the entry rather than be merged.
        public void Check(List<string> problems)
        {
            foreach (var key in collisions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(MaximumProblemsPerFile))
            {
                problems.Add($"Two different findings have the key '{key}': the tool, rule, path and fingerprint joined by '/' do not tell them apart.");
            }
        }

        // The data, with keys in ordinal order:
        // { "byRule": { "<tool>/<rule>": { "value": n } },
        //   "results": { "<key>": { "value": { "level", "line", "message", "occurrences", "path", "rule", "tool" } } },
        //   "total": { "value": n } }
        public string Write()
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, WriterOptions))
            {
                writer.WriteStartObject();
                writer.WriteStartObject("byRule");
                foreach (var (rule, count) in byRule)
                {
                    writer.WriteStartObject(rule);
                    writer.WriteString("value", count.ToString(CultureInfo.InvariantCulture));
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                writer.WriteStartObject("results");
                foreach (var (key, (finding, occurrences)) in results)
                {
                    writer.WriteStartObject(key);
                    writer.WriteStartObject("value");
                    writer.WriteString("level", finding.Level);
                    if (finding.Line is { } line)
                    {
                        writer.WriteString("line", line.ToString(CultureInfo.InvariantCulture));
                    }

                    writer.WriteString("message", finding.Message);
                    if (occurrences > 1)
                    {
                        writer.WriteString("occurrences", occurrences.ToString(CultureInfo.InvariantCulture));
                    }

                    writer.WriteString("path", finding.Path);
                    writer.WriteString("rule", finding.Rule);
                    writer.WriteString("tool", finding.Tool);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                writer.WriteStartObject("total");
                writer.WriteString("value", total.ToString(CultureInfo.InvariantCulture));
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    // A SARIF value that cannot be read as the converter needs it; the message is the problem.
    private sealed class SarifException(string message) : Exception(message);

    // The characters one entry's filled messages may still hold.
    private sealed class FillBudget(long limit)
    {
        public long Limit { get; } = limit;

        public long Remaining { get; set; } = limit;
    }

    // A rule descriptor, with its message strings by id once one is asked for.
    private sealed class RuleDescriptor(JsonElement element)
    {
        private Dictionary<string, JsonElement>? messages;

        public JsonElement Element => element;

        public string? MessageString(string id) =>
            (messages ??= Table(Object(element, "messageStrings"))).TryGetValue(id, out var entry) ? OptionalString(entry, "text") : null;
    }

    // A tool component, the driver or an extension, with its rules by index and by id once one is
    // asked for, so that each result's lookup does not walk the whole array.
    private sealed class Component(JsonElement element)
    {
        private RuleDescriptor?[]? rules;
        private Dictionary<string, RuleDescriptor>? byId;

        public RuleDescriptor?[] Rules => rules ??= element.TryGetProperty("rules", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Object ? new RuleDescriptor(item) : null)]
            : [];

        // The first rule with the id, as a walk of the array would find it. A rule whose id
        // cannot be read cannot match one that can, so it is passed over.
        public RuleDescriptor? ById(string id)
        {
            if (byId is null)
            {
                byId = new Dictionary<string, RuleDescriptor>(StringComparer.Ordinal);
                foreach (var rule in Rules.OfType<RuleDescriptor>())
                {
                    try
                    {
                        if (OptionalString(rule.Element, "id") is { } key)
                        {
                            byId.TryAdd(key, rule);
                        }
                    }
                    catch (SarifException)
                    {
                        // An id that is not valid UTF-8 is never the id a result names.
                    }
                }
            }

            return byId.GetValueOrDefault(id);
        }
    }

    // What a run's results look things up in, each read once per run.
    private sealed class RunTables
    {
        public RunTables(JsonElement run)
        {
            var tool = Object(run, "tool");
            Driver = Object(tool, "driver") is { } driver ? new Component(driver) : null;
            Extensions = tool is { } owner && owner.TryGetProperty("extensions", out var list) && list.ValueKind == JsonValueKind.Array
                ? [.. list.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Object ? new Component(item) : null)]
                : [];
            Artifacts = run.TryGetProperty("artifacts", out var artifacts) && artifacts.ValueKind == JsonValueKind.Array ? [.. artifacts.EnumerateArray()] : [];
            Bases = Table(Object(run, "originalUriBaseIds"));
        }

        public Component? Driver { get; }

        public Component?[] Extensions { get; }

        public JsonElement[] Artifacts { get; }

        public Dictionary<string, JsonElement> Bases { get; }
    }

    // Reads one SARIF file into the findings, adding a problem for anything it cannot read.
    private sealed class LogReader(string file, string root, int? maximumTextLength, Findings findings, FillBudget fill, List<string> problems)
    {
        public void ReadFile(byte[] bytes)
        {
            ReadOnlyMemory<byte> json = bytes;
            if (json.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            {
                json = json[3..];
            }

            if (!CheckSyntax(json.Span))
            {
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(json, DocumentOptions);
                ReadLog(document.RootElement);
            }
            catch (JsonException exception)
            {
                problems.Add($"'{file}' is not valid JSON: {exception.Message}");
            }
            catch (SarifException exception)
            {
                problems.Add($"'{file}': {exception.Message}");
            }
            catch (InvalidOperationException)
            {
                // System.Text.Json throws this for a property name it cannot decode, such as one
                // that is not valid UTF-8 or that escapes a lone surrogate.
                problems.Add($"'{file}' holds a property name that is not valid UTF-8 or valid Unicode; it was not read.");
            }
        }

        // Reads the file once as JSON without keeping it, so that nesting past the limit is named
        // as such rather than as malformed JSON.
        private bool CheckSyntax(ReadOnlySpan<byte> json)
        {
            var reader = new Utf8JsonReader(json, ReaderOptions);
            try
            {
                while (reader.Read())
                {
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && reader.CurrentDepth >= MaximumDepth)
                    {
                        problems.Add($"'{file}' nests deeper than {MaximumDepth.ToString(CultureInfo.InvariantCulture)} levels, the most a file may; it was not read.");
                        return false;
                    }
                }

                return true;
            }
            catch (JsonException exception)
            {
                problems.Add($"'{file}' is not valid JSON: {exception.Message}");
                return false;
            }
        }

        private void ReadLog(JsonElement log)
        {
            if (log.ValueKind != JsonValueKind.Object)
            {
                throw new SarifException("a SARIF log must be a JSON object.");
            }

            var version = log.TryGetProperty("version", out var given) ? given : default;
            if (version.ValueKind != JsonValueKind.String || Text(version) != Version)
            {
                var stated = version.ValueKind == JsonValueKind.String ? $"is SARIF version '{Cut(Text(version), 100)}'" : "states no SARIF version";
                throw new SarifException($"the file {stated}; only SARIF {Version} is read.");
            }

            if (!log.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
            {
                throw new SarifException(runs.ValueKind == JsonValueKind.Null
                    ? "'runs' is null, which SARIF uses when the tool could not run; no findings are recorded from it."
                    : "a SARIF log must have a 'runs' array.");
            }

            var number = 0;
            foreach (var run in runs.EnumerateArray())
            {
                ReadRun(run, number++);
            }
        }

        private void ReadRun(JsonElement run, int number)
        {
            var where = $"run {number.ToString(CultureInfo.InvariantCulture)}";
            if (run.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"'{file}' {where}: a run must be an object.");
                return;
            }

            var driver = Object(Object(run, "tool"), "driver");
            var name = driver is { } component && component.TryGetProperty("name", out var named) && named.ValueKind == JsonValueKind.String ? Text(named) : null;
            if (string.IsNullOrEmpty(name))
            {
                problems.Add($"'{file}' {where}: the run has no tool.driver.name, which names the tool.");
                return;
            }

            if (!IsKeyText(name))
            {
                problems.Add($"'{file}' {where}: the tool name must be valid Unicode with no control characters.");
                return;
            }

            if (!run.TryGetProperty("results", out var results) || results.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            if (results.ValueKind != JsonValueKind.Array)
            {
                problems.Add($"'{file}' {where}: 'results' must be an array.");
                return;
            }

            var tables = new RunTables(run);
            var index = 0;
            foreach (var result in results.EnumerateArray())
            {
                try
                {
                    findings.Add(ReadResult(tables, name, result));
                }
                catch (SarifException exception)
                {
                    problems.Add($"'{file}' {where} result {index.ToString(CultureInfo.InvariantCulture)}: {exception.Message}");
                }

                index++;
            }
        }

        private Finding ReadResult(RunTables tables, string tool, JsonElement result)
        {
            if (result.ValueKind != JsonValueKind.Object)
            {
                throw new SarifException("a result must be an object.");
            }

            var descriptor = Descriptor(tables, result, out var reference);
            var rule = OptionalString(result, "ruleId")
                ?? (reference is { } given ? OptionalString(given, "id") : null)
                ?? (descriptor is { } found ? OptionalString(found.Element, "id") : null);
            if (string.IsNullOrEmpty(rule))
            {
                throw new SarifException("the result names no rule: it has no ruleId, rule.id or rule descriptor with an id.");
            }

            if (!IsKeyText(rule))
            {
                throw new SarifException("the rule id must be valid Unicode with no control characters.");
            }

            var message = Message(result, descriptor);
            var fingerprint = Fingerprint(result, "fingerprints") ?? Fingerprint(result, "partialFingerprints") ?? MessageDigest(message);
            var (path, line) = Location(tables, result);
            return new Finding(tool, rule, path, fingerprint, Level(result, descriptor), line, Clean(message));
        }

        // The rule descriptor a result refers to: in the extension rule.toolComponent.index names,
        // or else in the driver; by rule.index or ruleIndex, or else by id.
        private static RuleDescriptor? Descriptor(RunTables tables, JsonElement result, out JsonElement? reference)
        {
            reference = Object(result, "rule");
            var component = tables.Driver;
            if (reference is { } given && Object(given, "toolComponent") is { } named && Integer(named, "index") is { } extension)
            {
                component = extension < tables.Extensions.Length ? tables.Extensions[(int)extension] : null;
            }

            if (component is null)
            {
                return null;
            }

            var index = (reference is { } r ? Integer(r, "index") : null) ?? Integer(result, "ruleIndex");
            if (index is { } at && at < component.Rules.Length)
            {
                return component.Rules[(int)at];
            }

            var id = OptionalString(result, "ruleId") ?? (reference is { } byId ? OptionalString(byId, "id") : null);
            return id is null ? null : component.ById(id);
        }

        // The result's level when given. Otherwise, as SARIF specifies, the rule's default level or
        // warning when the kind is absent or fail, and none for any other kind.
        private static string Level(JsonElement result, RuleDescriptor? descriptor)
        {
            if (OptionalString(result, "level") is { } level)
            {
                return Array.IndexOf(Levels, level) >= 0
                    ? level
                    : throw new SarifException($"the level '{Cut(level, 100)}' is not one of {string.Join(", ", Levels)}.");
            }

            var kind = OptionalString(result, "kind");
            if (kind is not (null or "fail"))
            {
                return "none";
            }

            if (descriptor is { } rule && OptionalString(Object(rule.Element, "defaultConfiguration"), "level") is { } fallback)
            {
                return Array.IndexOf(Levels, fallback) >= 0
                    ? fallback
                    : throw new SarifException($"the rule's default level '{Cut(fallback, 100)}' is not one of {string.Join(", ", Levels)}.");
            }

            return "warning";
        }

        // The message text, or the rule's message string the message id names, with its {n}
        // placeholders replaced by the message's arguments.
        private string Message(JsonElement result, RuleDescriptor? descriptor)
        {
            var message = Object(result, "message") ?? throw new SarifException("the result has no message.");
            if (OptionalString(message, "text") is { } text)
            {
                return text;
            }

            if (OptionalString(message, "id") is { } id
                && descriptor is { } rule
                && rule.MessageString(id) is { } format)
            {
                var arguments = message.TryGetProperty("arguments", out var given) && given.ValueKind == JsonValueKind.Array
                    ? given.EnumerateArray().Select(argument => argument.ValueKind == JsonValueKind.String ? Text(argument) : argument.GetRawText()).ToList()
                    : [];
                return Format(format, arguments);
            }

            throw new SarifException("the result's message has no text, and no message string of its rule that its id names.");
        }

        // SARIF's message format: {n} is the nth argument, and {{ and }} are literal braces. The
        // filled message counts against the entry's fill budget, checked before each argument is
        // added, so that repeating a long argument cannot build a message past it.
        private string Format(string format, List<string> arguments)
        {
            var builder = new StringBuilder(format.Length);
            for (var index = 0; index < format.Length; index++)
            {
                var character = format[index];
                if ((character == '{' || character == '}') && index + 1 < format.Length && format[index + 1] == character)
                {
                    builder.Append(character);
                    index++;
                    continue;
                }

                if (character == '{')
                {
                    var close = format.IndexOf('}', index + 1);
                    if (close > index + 1
                        && int.TryParse(format.AsSpan(index + 1, close - index - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                        && number < arguments.Count)
                    {
                        if (builder.Length + (long)arguments[number].Length > fill.Remaining)
                        {
                            throw new SarifException(FillProblem());
                        }

                        builder.Append(arguments[number]);
                        index = close;
                        continue;
                    }
                }

                builder.Append(character);
            }

            if (builder.Length > fill.Remaining)
            {
                throw new SarifException(FillProblem());
            }

            fill.Remaining -= builder.Length;
            return builder.ToString();
        }

        // The value of the ordinally first name in fingerprints or partialFingerprints.
        private static string? Fingerprint(JsonElement result, string field)
        {
            if (Object(result, field) is not { } values)
            {
                return null;
            }

            var first = values.EnumerateObject().Select(property => Name(property, field)).Order(StringComparer.Ordinal).FirstOrDefault();
            if (first is null)
            {
                return null;
            }

            var value = values.GetProperty(first);
            var text = value.ValueKind == JsonValueKind.String ? Text(value) : throw new SarifException($"the {field} value '{Cut(first, 100)}' is not a string.");
            return text.Length > 0 && IsKeyText(text)
                ? text
                : throw new SarifException($"the {field} value '{Cut(first, 100)}' must be non-empty, valid Unicode with no control characters.");
        }

        private string FillProblem() =>
            $"the messages filled from their arguments would hold more than {fill.Limit.ToString("N0", CultureInfo.InvariantCulture)} characters, the most one entry may.";

        // A property's name. A name that is not valid UTF-8 cannot be read, and is a problem.
        private static string Name(JsonProperty property, string field)
        {
            try
            {
                return property.Name;
            }
            catch (InvalidOperationException)
            {
                throw new SarifException($"a {field} name is not valid UTF-8.");
            }
        }

        // The SHA-256 of the message text with its digits removed, so that a number in the
        // message, such as a count or a line, does not change the finding.
        private static string MessageDigest(string message)
        {
            var text = new string([.. message.Where(character => !char.IsAsciiDigit(character))]);
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SourceFiles.Display(text))));
        }

        // The repository-relative path and start line of the result's first location.
        private (string Path, long? Line) Location(RunTables tables, JsonElement result)
        {
            if (!result.TryGetProperty("locations", out var locations) || locations.ValueKind != JsonValueKind.Array || locations.GetArrayLength() == 0)
            {
                return (NoPath, null);
            }

            var physical = Object(locations[0], "physicalLocation");
            long? line = null;
            if (Object(physical, "region") is { } region && region.TryGetProperty("startLine", out var start))
            {
                line = start.ValueKind == JsonValueKind.Number && start.TryGetInt64(out var number) && number >= 1
                    ? number
                    : throw new SarifException("the region's startLine must be a whole number of at least 1.");
            }

            var artifact = Object(physical, "artifactLocation");
            if (artifact is { } location && OptionalString(location, "uri") is null && Integer(location, "index") is { } index && index < tables.Artifacts.Length)
            {
                artifact = Object(tables.Artifacts[(int)index], "location");
            }

            if (artifact is not { } found || OptionalString(found, "uri") is not { } uri)
            {
                return (NoPath, line);
            }

            try
            {
                return (SarifPath.Resolve(uri, OptionalString(found, "uriBaseId"), tables.Bases, root), line);
            }
            catch (Exception exception) when (exception is FormatException or InvalidOperationException)
            {
                throw new SarifException(exception is FormatException ? exception.Message : "a string in the file is not valid UTF-8.");
            }
        }

        // Message text as it is recorded: each control character becomes a space, a lone surrogate
        // becomes U+FFFD, and text over maxTextLength is cut to end with an ellipsis.
        private string Clean(string message)
        {
            var runes = SourceFiles.Display(message).EnumerateRunes().Select(rune => Rune.IsControl(rune) ? new Rune(' ') : rune).ToList();
            if (maximumTextLength is { } limit && runes.Count > limit)
            {
                runes = [.. runes.Take(limit - 1), new Rune('…')];
            }

            var builder = new StringBuilder(runes.Count);
            foreach (var rune in runes)
            {
                builder.Append(rune.ToString());
            }

            return builder.ToString();
        }
    }

    // Text that may be part of a key: valid Unicode with no control character.
    private static bool IsKeyText(string text) => !SourceFiles.HasLoneSurrogate(text) && !text.EnumerateRunes().Any(Rune.IsControl);

    // An object's members by name, for lookups repeated once per result. A name that is not valid
    // UTF-8 cannot be named by any string read, so it is passed over.
    private static Dictionary<string, JsonElement> Table(JsonElement? parent)
    {
        var table = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (parent is not { ValueKind: JsonValueKind.Object } element)
        {
            return table;
        }

        foreach (var property in element.EnumerateObject())
        {
            try
            {
                table.TryAdd(property.Name, property.Value);
            }
            catch (InvalidOperationException)
            {
                // Passed over, as above.
            }
        }

        return table;
    }

    private static JsonElement? Object(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } element && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? OptionalString(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } element && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Text(value)
            : null;

    private static long? Integer(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number) && number >= 0 && number <= int.MaxValue
            ? number
            : null;

    // A JSON string's text. A string that is not valid UTF-8 cannot be read, and is a problem.
    private static string Text(JsonElement value)
    {
        try
        {
            return value.GetString()!;
        }
        catch (InvalidOperationException)
        {
            throw new SarifException("a string in the file is not valid UTF-8.");
        }
    }

    private static string Cut(string text, int length) =>
        SourceFiles.Display(text.Length <= length ? text : text[..length] + "…");
}
