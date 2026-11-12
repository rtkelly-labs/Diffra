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

    // The one SARIF version it reads.
    private const string Version = "2.1.0";

    /// <summary>The most bytes one file may hold: 64 MiB.</summary>
    public const long MaximumFileBytes = 64L * 1024 * 1024;

    /// <summary>The most bytes the files of one entry may hold together: 256 MiB.</summary>
    public const long MaximumEntryBytes = 256L * 1024 * 1024;

    // How deep a SARIF file may nest.
    private const int MaximumDepth = 256;

    // The most digits a message placeholder such as {0} may have; an int has at most ten.
    private const int MaximumPlaceholderDigits = 10;

    // The path of a result that has no location.
    private const string NoPath = "<none>";

    /// <summary>
    /// The most characters the messages filled from their arguments may hold together in one
    /// entry, so that a short message string repeating a long argument cannot fill memory.
    /// </summary>
    public const long MaximumFilledCharacters = 64L * 1024 * 1024;

    /// <summary>
    /// The most characters of keys and messages the findings of one entry may keep, so that many
    /// results with a long tool name, rule id or message cannot fill memory before the
    /// <c>maxKeys</c> budget is checked.
    /// </summary>
    public const long MaximumKeptCharacters = 64L * 1024 * 1024;

    /// <summary>
    /// The work one file may take for each of its bytes, counted as the characters read, decoded,
    /// scanned or built, so that a file built to make the reader repeat itself is stopped after
    /// work proportional to its length.
    /// </summary>
    public const long WorkPerByte = 16;

    /// <summary>The work every file may take besides, whatever its length: 1 Mi characters.</summary>
    public const long WorkAllowance = 1024 * 1024;

    /// <summary>The most characters a tool name, rule id or fingerprint may hold.</summary>
    public const int MaximumKeyPartLength = 1024;

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

    internal static ConverterResult Read(
        string root,
        string glob,
        int? maximumKeys,
        int? maximumTextLength,
        long fileLimit,
        long entryLimit,
        long fillLimit = MaximumFilledCharacters,
        long keptLimit = MaximumKeptCharacters,
        long workAllowance = WorkAllowance)
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
        var findings = new Findings(keptLimit);
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
            var fileProblems = new FileProblems();
            var work = new WorkBudget(bytes.Length, workAllowance);
            new LogReader(path, root, maximumTextLength, findings, fill, work, fileProblems).ReadFile(bytes);
            fileProblems.CopyTo(problems, path);
            if (fill.Exceeded || findings.Exceeded || work.Exceeded)
            {
                // The entry is rejected; reading on would only report the same limit again.
                break;
            }
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
        public string Key { get; } = $"{Tool}/{Rule}/{Path}/{Fingerprint}";
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
    private sealed class Findings(long keptLimit)
    {
        private long kept;
        private readonly SortedDictionary<string, (Finding Kept, int Occurrences)> results = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, (string Tool, string Rule, int Count)> byRule = new(StringComparer.Ordinal);
        private readonly SortedSet<string> collisions = new(StringComparer.Ordinal);
        private readonly SortedSet<string> ruleCollisions = new(StringComparer.Ordinal);
        private int total;

        public int Keys => results.Count;

        // Whether the findings would keep more characters than the entry may; reading then stops.
        public bool Exceeded { get; private set; }

        public void Add(Finding finding)
        {
            var rule = $"{finding.Tool}/{finding.Rule}";
            var key = finding.Key;
            var exists = results.TryGetValue(key, out var held);
            var chosen = exists && Precedence.Instance.Compare(finding, held.Kept) >= 0 ? held.Kept : finding;
            var more = (byRule.ContainsKey(rule) ? 0L : rule.Length)
                + (exists
                    ? chosen.Message.Length - held.Kept.Message.Length
                    : key.Length + finding.Path.Length + finding.Fingerprint.Length + finding.Message.Length);
            if (kept + more > keptLimit)
            {
                Exceeded = true;
                throw new SarifException($"the findings would keep more than {keptLimit.ToString("N0", CultureInfo.InvariantCulture)} characters of keys and messages, the most one entry may.");
            }

            kept += more;
            total++;
            if (byRule.TryGetValue(rule, out var counted))
            {
                if (counted.Tool != finding.Tool || counted.Rule != finding.Rule)
                {
                    ruleCollisions.Add(rule);
                }

                byRule[rule] = counted with { Count = counted.Count + 1 };
            }
            else
            {
                byRule[rule] = (finding.Tool, finding.Rule, 1);
            }

            if (!exists)
            {
                results[key] = (finding, 1);
                return;
            }

            if (held.Kept.Tool != finding.Tool || held.Kept.Rule != finding.Rule || held.Kept.Path != finding.Path || held.Kept.Fingerprint != finding.Fingerprint)
            {
                collisions.Add(key);
            }

            results[key] = (chosen, held.Occurrences + 1);
        }

        // Two different findings whose parts join to the same key, or two tool and rule pairs
        // that join to the same byRule key, could only be told apart by a key the spec does not
        // define, so they reject the entry rather than be merged.
        public void Check(List<string> problems)
        {
            foreach (var key in ruleCollisions.Take(MaximumProblemsPerFile))
            {
                problems.Add($"Two different tool and rule pairs have the byRule key '{key}': the tool and rule joined by '/' do not tell them apart.");
            }

            foreach (var key in collisions.Take(MaximumProblemsPerFile))
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
                foreach (var (rule, (_, _, count)) in byRule)
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

    // Reading a file would take more work than it may; the message is the problem. It is not a
    // SarifException, so that nothing that passes over a value it cannot read passes over it too.
    private sealed class WorkException(string message) : Exception(message);

    // The work one file may take. Each character read, decoded, scanned or built is charged as it
    // is, so that a file whose values make the reader repeat itself is stopped in time.
    private sealed class WorkBudget(long bytes, long allowance)
    {
        public long Limit { get; } = (WorkPerByte * bytes) + allowance;

        public long Used { get; private set; }

        // Whether the file would take more work than it may; reading then stops.
        public bool Exceeded { get; private set; }

        public void Charge(long characters)
        {
            Used += characters;
            if (Used > Limit)
            {
                Exceeded = true;
                throw new WorkException(
                    $"reading it would take more than {Limit.ToString("N0", CultureInfo.InvariantCulture)} characters of work, the most a file of {bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes may "
                    + $"({WorkPerByte.ToString(CultureInfo.InvariantCulture)} for each byte and {allowance.ToString("N0", CultureInfo.InvariantCulture)} more); it was not read further.");
            }
        }
    }

    // One file's problems: the first few are kept as they are found and the rest only counted, so
    // that a file of many bad results cannot hold a problem for each in memory.
    private sealed class FileProblems
    {
        private readonly List<string> kept = [];
        private int omitted;

        public void Add(string problem)
        {
            if (kept.Count < MaximumProblemsPerFile)
            {
                kept.Add(problem);
            }
            else
            {
                omitted++;
            }
        }

        public void CopyTo(List<string> problems, string path)
        {
            problems.AddRange(kept);
            if (omitted > 0)
            {
                problems.Add($"'{path}' has {omitted.ToString(CultureInfo.InvariantCulture)} more problems that are not listed.");
            }
        }
    }

    // The characters one entry's filled messages may still hold.
    private sealed class FillBudget(long limit)
    {
        public long Limit { get; } = limit;

        public long Remaining { get; set; } = limit;

        // Whether a message has been found that would pass the limit; reading then stops.
        public bool Exceeded { get; set; }
    }

    // A value read once, when it is first asked for. A value that cannot be read gives the same
    // problem each time it is asked for, without being read again.
    private sealed class Once<T>(Func<T> read)
    {
        private (T Value, string? Problem)? outcome;

        public T Value
        {
            get
            {
                if (outcome is null)
                {
                    try
                    {
                        outcome = (read(), null);
                    }
                    catch (SarifException exception)
                    {
                        outcome = (default(T)!, exception.Message);
                    }
                }

                return outcome.Value.Problem is { } problem ? throw new SarifException(problem) : outcome.Value.Value;
            }
        }
    }

    // Message strings by id, each read once, when it is first asked for.
    private sealed class MessageStrings(JsonElement? parent, WorkBudget work)
    {
        private readonly Dictionary<string, Once<string?>> read = new(StringComparer.Ordinal);

        private Dictionary<string, JsonElement> Entries => field ??= Table(work, parent);

        public string? Find(string id)
        {
            if (!read.TryGetValue(id, out var text))
            {
                text = new Once<string?>(() => Entries.TryGetValue(id, out var entry) ? OptionalString(work, entry, "text") : null);
                read[id] = text;
            }

            return text.Value;
        }
    }

    // A rule descriptor, with its id, default level and message strings each read once.
    private sealed class RuleDescriptor(JsonElement element, Component owner, WorkBudget work)
    {
        // The tool component whose rules hold this descriptor.
        public Component Owner => owner;

        public Once<string?> Id { get; } = new(() => OptionalString(work, element, "id"));

        public Once<string?> DefaultLevel { get; } = new(() => OptionalString(work, Object(element, "defaultConfiguration"), "level"));

        public MessageStrings Messages => field ??= new MessageStrings(Object(element, "messageStrings"), work);
    }

    // A tool component, the driver or an extension, with its rules by index and by id once one is
    // asked for, so that each result's lookup does not walk the whole array.
    private sealed class Component(JsonElement element, WorkBudget work)
    {
        private Dictionary<string, RuleDescriptor>? byId;

        public RuleDescriptor?[] Rules => field ??= element.TryGetProperty("rules", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Object ? new RuleDescriptor(item, this, work) : null)]
            : [];

        // The message strings of the component as a whole, which SARIF 2.1.0 §3.11.7 looks in
        // for a result's message after the rule's own.
        public MessageStrings GlobalMessages => field ??= new MessageStrings(Object(element, "globalMessageStrings"), work);

        // The component's guid, or none when it has none that can be read.
        public string? ReadGuid()
        {
            try
            {
                return OptionalString(work, element, "guid");
            }
            catch (SarifException)
            {
                return null;
            }
        }

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
                        if (rule.Id.Value is { } key)
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

    // What a run's results look things up in, each read once per run, so that a result's cost
    // depends on the result and not on how large the run's shared parts are.
    private sealed class RunTables
    {
        private readonly WorkBudget work;
        private readonly Once<Dictionary<RuleDescriptor, string?>>?[] overrides;
        private readonly Once<string?>?[] artifactPaths;

        public RunTables(JsonElement run, WorkBudget work)
        {
            this.work = work;
            var tool = Object(run, "tool");
            Driver = Object(tool, "driver") is { } driver ? new Component(driver, work) : null;
            Extensions = tool is { } owner && owner.TryGetProperty("extensions", out var list) && list.ValueKind == JsonValueKind.Array
                ? [.. list.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Object ? new Component(item, work) : null)]
                : [];
            Artifacts = run.TryGetProperty("artifacts", out var artifacts) && artifacts.ValueKind == JsonValueKind.Array ? [.. artifacts.EnumerateArray()] : [];
            artifactPaths = new Once<string?>?[Artifacts.Length];
            Bases = ReadBases(Table(work, Object(run, "originalUriBaseIds")));
            Invocations = run.TryGetProperty("invocations", out var invocations) && invocations.ValueKind == JsonValueKind.Array ? [.. invocations.EnumerateArray()] : [];
            overrides = new Once<Dictionary<RuleDescriptor, string?>>?[Invocations.Length];
        }

        private Component? Driver { get; }

        private Component?[] Extensions { get; }

        private JsonElement[] Artifacts { get; }

        public Dictionary<string, SarifBase> Bases { get; }

        private JsonElement[] Invocations { get; }

        // The driver and extensions by guid, ignoring case; the first with a guid is kept.
        private Dictionary<string, Component> Guids => field ??= ReadGuids();

        // The invocation a result's provenance names. Without one, SARIF 2.1.0 §3.48.6 takes the
        // run's only invocation when it has exactly one, and otherwise none.
        public long? InvocationFor(JsonElement result) =>
            Object(result, "provenance") is { } provenance && Integer(provenance, "invocationIndex") is { } given
                ? given
                : Invocations.Length == 1 ? 0 : null;

        // The tool component a toolComponent reference names: by index among the extensions, else
        // by guid among the driver and the extensions, else the driver. A reference that names
        // no component the run defines gives none, so that no other component's rule is used.
        public Component? ComponentFor(JsonElement? reference)
        {
            if (reference is not { } named)
            {
                return Driver;
            }

            if (Integer(named, "index") is { } index)
            {
                return index < Extensions.Length ? Extensions[(int)index] : null;
            }

            return OptionalString(work, named, "guid") is { } guid ? Guids.GetValueOrDefault(guid) : Driver;
        }

        // The rule descriptor a reporting descriptor reference names in a component: by index,
        // else by id.
        public static RuleDescriptor? DescriptorFor(Component? component, long? index, string? id)
        {
            if (component is null)
            {
                return null;
            }

            if (index is { } at && at < component.Rules.Length)
            {
                return component.Rules[(int)at];
            }

            return id is null ? null : component.ById(id);
        }

        // The level an invocation's ruleConfigurationOverrides give a rule, if any. As SARIF 2.1.0
        // §3.27.10 reads, the override that names the rule decides, and one that gives no level
        // leaves the rule's default level; when several name the rule, the first is used.
        public string? OverrideLevel(long invocation, RuleDescriptor descriptor)
        {
            if (invocation >= Invocations.Length)
            {
                return null;
            }

            var table = overrides[invocation] ??= new Once<Dictionary<RuleDescriptor, string?>>(() => ReadOverrides(Invocations[(int)invocation]));
            return table.Value.GetValueOrDefault(descriptor);
        }

        // The recorded path of the artifact the index names, resolved once for every result that
        // names it; none when the run has no such artifact or it has no URI.
        public string? ArtifactPath(long index, Func<string, string?, string> resolve)
        {
            if (index >= Artifacts.Length)
            {
                return null;
            }

            var path = artifactPaths[index] ??= new Once<string?>(() =>
                Object(Artifacts[(int)index], "location") is { } location && OptionalString(work, location, "uri") is { } uri
                    ? resolve(uri, OptionalString(work, location, "uriBaseId"))
                    : null);
            return path.Value;
        }

        private Dictionary<string, Component> ReadGuids()
        {
            var table = new Dictionary<string, Component>(StringComparer.OrdinalIgnoreCase);
            foreach (var component in Extensions.Prepend(Driver).OfType<Component>())
            {
                if (component.ReadGuid() is { } guid)
                {
                    table.TryAdd(guid, component);
                }
            }

            return table;
        }

        // The run's originalUriBaseIds, each read once. One that cannot be read is a problem only
        // when a location uses it.
        private Dictionary<string, SarifBase> ReadBases(Dictionary<string, JsonElement> entries)
        {
            var bases = new Dictionary<string, SarifBase>(StringComparer.Ordinal);
            foreach (var (name, entry) in entries)
            {
                try
                {
                    bases[name] = new SarifBase(OptionalString(work, entry, "uri"), OptionalString(work, entry, "uriBaseId"), null);
                }
                catch (SarifException exception)
                {
                    bases[name] = new SarifBase(null, null, exception.Message);
                }
            }

            return bases;
        }

        private Dictionary<RuleDescriptor, string?> ReadOverrides(JsonElement invocation)
        {
            var table = new Dictionary<RuleDescriptor, string?>();
            if (invocation.ValueKind != JsonValueKind.Object
                || !invocation.TryGetProperty("ruleConfigurationOverrides", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return table;
            }

            foreach (var item in list.EnumerateArray())
            {
                if (Object(item, "descriptor") is { } named
                    && DescriptorFor(ComponentFor(Object(named, "toolComponent")), Integer(named, "index"), OptionalString(work, named, "id")) is { } rule)
                {
                    table.TryAdd(rule, OptionalString(work, Object(item, "configuration"), "level"));
                }
            }

            return table;
        }
    }

    // Reads one SARIF file into the findings, adding a problem for anything it cannot read.
    private sealed class LogReader(string file, string root, int? maximumTextLength, Findings findings, FillBudget fill, WorkBudget work, FileProblems problems)
    {
        // Whether a limit has been passed that rejects the entry; reading then stops.
        private bool Stopped => fill.Exceeded || findings.Exceeded || work.Exceeded;

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
            catch (Exception exception) when (exception is SarifException or WorkException)
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
            if (version.ValueKind != JsonValueKind.String || Text(work, version) != Version)
            {
                var stated = version.ValueKind == JsonValueKind.String ? $"is SARIF version '{Cut(Text(work, version), 100)}'" : "states no SARIF version";
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
                if (Stopped)
                {
                    return;
                }
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
            var name = driver is { } component && component.TryGetProperty("name", out var named) && named.ValueKind == JsonValueKind.String ? Text(work, named) : null;
            if (string.IsNullOrEmpty(name))
            {
                problems.Add($"'{file}' {where}: the run has no tool.driver.name, which names the tool.");
                return;
            }

            if (name.Length > MaximumKeyPartLength)
            {
                problems.Add($"'{file}' {where}: the tool name is {name.Length.ToString("N0", CultureInfo.InvariantCulture)} characters, more than the {MaximumKeyPartLength.ToString("N0", CultureInfo.InvariantCulture)} a key part may hold.");
                return;
            }

            if (!IsKeyText(name))
            {
                problems.Add($"'{file}' {where}: the tool name must be valid Unicode with no control characters.");
                return;
            }

            // Results kept in external property files are not read, since no URI is followed; a
            // run that has them would otherwise record too few findings.
            if (Object(run, "externalPropertyFileReferences") is { } external && external.TryGetProperty("results", out var referenced)
                && referenced.ValueKind != JsonValueKind.Null && (referenced.ValueKind != JsonValueKind.Array || referenced.GetArrayLength() > 0))
            {
                problems.Add($"'{file}' {where}: the run keeps results in external property files, which are not read.");
                return;
            }

            // SARIF leaves results absent or null when the analysis did not start, and gives an
            // empty array when it ran and found nothing.
            if (!run.TryGetProperty("results", out var results) || results.ValueKind == JsonValueKind.Null)
            {
                problems.Add($"'{file}' {where}: the run has no results, which SARIF uses when the analysis did not start; no findings are recorded from it.");
                return;
            }

            if (results.ValueKind != JsonValueKind.Array)
            {
                problems.Add($"'{file}' {where}: 'results' must be an array.");
                return;
            }

            var tables = new RunTables(run, work);
            var index = 0;
            foreach (var result in results.EnumerateArray())
            {
                try
                {
                    var finding = ReadResult(tables, name, result);
                    work.Charge(finding.Key.Length + finding.Tool.Length + finding.Rule.Length);
                    findings.Add(finding);
                }
                catch (Exception exception) when (exception is SarifException or WorkException)
                {
                    problems.Add($"'{file}' {where} result {index.ToString(CultureInfo.InvariantCulture)}: {exception.Message}");
                    if (Stopped)
                    {
                        return;
                    }
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

            var descriptor = Descriptor(tables, result, out var reference, out var component);
            var rule = OptionalString(work, result, "ruleId")
                ?? (reference is { } given ? OptionalString(work, given, "id") : null)
                ?? descriptor?.Id.Value;
            if (string.IsNullOrEmpty(rule))
            {
                throw new SarifException("the result names no rule: it has no ruleId, rule.id or rule descriptor with an id.");
            }

            if (rule.Length > MaximumKeyPartLength)
            {
                throw new SarifException($"the rule id is {rule.Length.ToString("N0", CultureInfo.InvariantCulture)} characters, more than the {MaximumKeyPartLength.ToString("N0", CultureInfo.InvariantCulture)} a key part may hold.");
            }

            if (!IsKeyText(rule))
            {
                throw new SarifException("the rule id must be valid Unicode with no control characters.");
            }

            var message = Message(result, descriptor, component);
            var fingerprint = Fingerprint(result, "fingerprints") ?? Fingerprint(result, "partialFingerprints") ?? MessageDigest(message);
            var (path, line) = Location(tables, result);
            return new Finding(tool, rule, path, fingerprint, Level(tables, result, descriptor), line, Clean(message));
        }

        // The rule descriptor a result refers to, in the component rule.toolComponent names (by
        // index, else guid) or else in the driver; by rule.index or ruleIndex, or else by id. The
        // component is given too, for its global message strings.
        private RuleDescriptor? Descriptor(RunTables tables, JsonElement result, out JsonElement? reference, out Component? component)
        {
            reference = Object(result, "rule");
            component = tables.ComponentFor(reference is { } given ? Object(given, "toolComponent") : null);
            var index = (reference is { } r ? Integer(r, "index") : null) ?? Integer(result, "ruleIndex");
            var id = OptionalString(work, result, "ruleId") ?? (reference is { } byId ? OptionalString(work, byId, "id") : null);
            return RunTables.DescriptorFor(component, index, id);
        }

        // The result's level when given. Otherwise, as SARIF 2.1.0 §3.27.10 specifies, when the
        // kind is absent or fail: the level the override for the rule in the result's invocation
        // gives, else the rule's default level, else warning; and none for any other kind.
        private string Level(RunTables tables, JsonElement result, RuleDescriptor? descriptor)
        {
            if (OptionalString(work, result, "level") is { } level)
            {
                return Array.IndexOf(Levels, level) >= 0
                    ? level
                    : throw new SarifException($"the level '{Cut(level, 100)}' is not one of {string.Join(", ", Levels)}.");
            }

            var kind = OptionalString(work, result, "kind");
            if (kind is not (null or "fail"))
            {
                return "none";
            }

            if (descriptor is { } overridden
                && tables.InvocationFor(result) is { } invocation
                && tables.OverrideLevel(invocation, overridden) is { } configured)
            {
                return Array.IndexOf(Levels, configured) >= 0
                    ? configured
                    : throw new SarifException($"the overriding level '{Cut(configured, 100)}' is not one of {string.Join(", ", Levels)}.");
            }

            if (descriptor?.DefaultLevel.Value is { } fallback)
            {
                return Array.IndexOf(Levels, fallback) >= 0
                    ? fallback
                    : throw new SarifException($"the rule's default level '{Cut(fallback, 100)}' is not one of {string.Join(", ", Levels)}.");
            }

            return "warning";
        }

        // The message text, or the message string the message id names, with its {n} placeholders
        // replaced by the message's arguments.
        private string Message(JsonElement result, RuleDescriptor? descriptor, Component? component)
        {
            var message = Object(result, "message") ?? throw new SarifException("the result has no message.");
            List<string>? arguments = message.TryGetProperty("arguments", out var given) && given.ValueKind == JsonValueKind.Array
                ? [.. given.EnumerateArray().Select(Argument)]
                : null;

            // Text with no arguments is kept as written, so that braces in it stay as they are.
            if (OptionalString(work, message, "text") is { } text)
            {
                return arguments is null ? text : Format(text, arguments);
            }

            // As SARIF 2.1.0 §3.11.7 looks a message string up: in the rule's messageStrings, then
            // in the globalMessageStrings of the component that defines the rule.
            if (OptionalString(work, message, "id") is { } id
                && (descriptor?.Messages.Find(id) ?? (descriptor?.Owner ?? component)?.GlobalMessages.Find(id)) is { } format)
            {
                return Format(format, arguments ?? []);
            }

            throw new SarifException("the result's message has no text, and no message string of its rule or tool component that its id names.");
        }

        // A message argument as text: a string's value, or any other value as written.
        private string Argument(JsonElement argument)
        {
            if (argument.ValueKind == JsonValueKind.String)
            {
                return Text(work, argument);
            }

            var raw = argument.GetRawText();
            work.Charge(raw.Length);
            return raw;
        }

        // SARIF's message format: {n} is the nth argument, and {{ and }} are literal braces. The
        // filled message counts against the entry's fill budget, checked before each character or
        // argument is added, so that repeating a long argument cannot build a message past it.
        // Reading the format is charged as work: each character is looked at at most twice, once
        // as a placeholder's digit and once as text.
        private string Format(string format, List<string> arguments)
        {
            work.Charge(2L * format.Length);
            var builder = new StringBuilder((int)Math.Min(format.Length, fill.Remaining));
            for (var index = 0; index < format.Length; index++)
            {
                var character = format[index];
                if ((character == '{' || character == '}') && index + 1 < format.Length && format[index + 1] == character)
                {
                    Reserve(builder, 1);
                    builder.Append(character);
                    index++;
                    continue;
                }

                // A placeholder is '{', a few digits and '}'. Only that far is looked at, so a text
                // of many '{' with no '}' is read once; anything else is kept as written.
                if (character == '{')
                {
                    var close = index + 1;
                    while (close < format.Length && close - index <= MaximumPlaceholderDigits && char.IsAsciiDigit(format[close]))
                    {
                        close++;
                    }

                    if (close > index + 1
                        && close < format.Length
                        && format[close] == '}'
                        && int.TryParse(format.AsSpan(index + 1, close - index - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                        && number < arguments.Count)
                    {
                        Reserve(builder, arguments[number].Length);
                        work.Charge(arguments[number].Length);
                        builder.Append(arguments[number]);
                        index = close;
                        continue;
                    }
                }

                Reserve(builder, 1);
                builder.Append(character);
            }

            fill.Remaining -= builder.Length;
            return builder.ToString();
        }

        // Checks, before characters are added, that the filled message stays within what the
        // entry may still fill.
        private void Reserve(StringBuilder builder, long more)
        {
            if (builder.Length + more > fill.Remaining)
            {
                fill.Exceeded = true;
                throw new SarifException(FillProblem());
            }
        }

        // The value of the ordinally first name in fingerprints or partialFingerprints.
        private string? Fingerprint(JsonElement result, string field)
        {
            if (Object(result, field) is not { } values)
            {
                return null;
            }

            string? first = null;
            JsonElement value = default;
            foreach (var property in values.EnumerateObject())
            {
                var name = PropertyName(property, field);
                if (first is null || string.CompareOrdinal(name, first) < 0)
                {
                    first = name;
                    value = property.Value;
                }
            }

            if (first is null)
            {
                return null;
            }

            var text = value.ValueKind == JsonValueKind.String ? Text(work, value) : throw new SarifException($"the {field} value '{Cut(first, 100)}' is not a string.");
            if (text.Length > MaximumKeyPartLength)
            {
                throw new SarifException($"the {field} value '{Cut(first, 100)}' is {text.Length.ToString("N0", CultureInfo.InvariantCulture)} characters, more than the {MaximumKeyPartLength.ToString("N0", CultureInfo.InvariantCulture)} a key part may hold.");
            }

            return text.Length > 0 && IsKeyText(text)
                ? text
                : throw new SarifException($"the {field} value '{Cut(first, 100)}' must be non-empty, valid Unicode with no control characters.");
        }

        private string FillProblem() =>
            $"the messages filled from their arguments would hold more than {fill.Limit.ToString("N0", CultureInfo.InvariantCulture)} characters, the most one entry may.";

        // A property's name. A name that is not valid UTF-8 cannot be read, and is a problem.
        private string PropertyName(JsonProperty property, string field)
        {
            string name;
            try
            {
                name = property.Name;
            }
            catch (InvalidOperationException)
            {
                throw new SarifException($"a {field} name is not valid UTF-8.");
            }

            work.Charge(name.Length);
            return name;
        }

        // The SHA-256 of the message text with its digits removed, so that a number in the
        // message, such as a count or a line, does not change the finding.
        private string MessageDigest(string message)
        {
            work.Charge(message.Length);
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

            if (Object(physical, "artifactLocation") is not { } location)
            {
                return (NoPath, line);
            }

            // A location with no URI of its own takes the one of the artifact its index names.
            var path = OptionalString(work, location, "uri") is { } uri
                ? Resolve(tables, uri, OptionalString(work, location, "uriBaseId"))
                : Integer(location, "index") is { } index ? tables.ArtifactPath(index, (named, baseId) => Resolve(tables, named, baseId)) : null;
            return (path ?? NoPath, line);
        }

        private string Resolve(RunTables tables, string uri, string? uriBaseId)
        {
            try
            {
                return SarifPath.Resolve(uri, uriBaseId, tables.Bases, root, work.Charge);
            }
            catch (FormatException exception)
            {
                throw new SarifException(exception.Message);
            }
        }

        // Message text as it is recorded: each control character becomes a space, a lone surrogate
        // becomes U+FFFD, and text over maxTextLength is cut to end with an ellipsis.
        private string Clean(string message)
        {
            work.Charge(message.Length);
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
    private static Dictionary<string, JsonElement> Table(WorkBudget work, JsonElement? parent)
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
                var name = property.Name;
                work.Charge(name.Length);
                table.TryAdd(name, property.Value);
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

    private static string? OptionalString(WorkBudget work, JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } element && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Text(work, value)
            : null;

    private static long? Integer(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number) && number is >= 0 and <= int.MaxValue
            ? number
            : null;

    // A JSON string's text, charged as work. A string that is not valid UTF-8 cannot be read, and
    // is a problem.
    private static string Text(WorkBudget work, JsonElement value)
    {
        string text;
        try
        {
            text = value.GetString()!;
        }
        catch (InvalidOperationException)
        {
            throw new SarifException("a string in the file is not valid UTF-8.");
        }

        work.Charge(text.Length);
        return text;
    }

    private static string Cut(string text, int length) =>
        SourceFiles.Display(text.Length <= length ? text : text[..length] + "…");
}
