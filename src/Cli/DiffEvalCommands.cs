using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Diffra.Protocol;

internal static class DiffEvalCommands
{
    private const string EvidenceSchema = "https://diffra.dev/schemas/evidence-v1.schema.json";
    private const string BaselineSchema = "https://diffra.dev/schemas/baseline-reference-v1.schema.json";
    private const string DeltaSchema = "https://diffra.dev/schemas/delta-v1.schema.json";
    private static readonly SearchValues<char> NonLowercaseHexCharacters = SearchValues.Create("0123456789abcdef");
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static int RunDiff(string[] args)
    {
        try
        {
            var options = ParseArguments(args,
                ("--baseline-ref", true), ("--baseline-evidence", true), ("--evidence", true), ("-o", true));
            var baselineReferenceBytes = ReadBounded(options["--baseline-ref"]);
            var baselineEvidenceBytes = ReadBounded(options["--baseline-evidence"]);
            var candidateEvidenceBytes = ReadBounded(options["--evidence"]);
            var outputPath = options["-o"];
            EnsureOutputIsDistinct(outputPath, [options["--baseline-ref"], options["--baseline-evidence"], options["--evidence"]]);

            var baselineEvidence = ReadAndVerifyEvidence(baselineEvidenceBytes, options["--baseline-evidence"]);
            var candidateEvidence = ReadAndVerifyEvidence(candidateEvidenceBytes, options["--evidence"]);
            var baselineReference = ReadAndVerifyBaselineReference(baselineReferenceBytes, baselineEvidence);
            EnsureSameSubject(baselineEvidence.RootElement, candidateEvidence.RootElement);

            using var baseline = baselineEvidence;
            using var candidate = candidateEvidence;
            var typedBaseline = ToNumericEvidence(baseline.RootElement);
            var typedCandidate = ToNumericEvidence(candidate.RootElement);
            var delta = StructuredNumericComparator.Compare(
                typedBaseline,
                typedCandidate,
                baselineReference);
            var output = JsonSerializer.SerializeToUtf8Bytes(delta, WebJson);
            DocumentIdentity.Verify("delta", output);
            WriteAtomically(outputPath, output);

            Console.Error.WriteLine("Local baseline-reference files are not safe to accept from untrusted PR input; the trust record is checked for structure and identity but is not authenticated. This command does not promote baselines.");
            return 0;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            PrintDiffUsage();
            return 2;
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine($"Invalid diff input: {exception.Message}");
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine($"Diff failed: {exception.Message}");
            return 1;
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
        {
            Console.Error.WriteLine($"Invalid diff input: {exception.Message}");
            return 2;
        }
    }

    public static int RunEval(string[] args)
    {
        try
        {
            var options = ParseArguments(args, ("--rules", true), ("--delta", true), ("-o", true));
            var rulesBytes = ReadBounded(options["--rules"]);
            var deltaBytes = ReadBounded(options["--delta"]);
            var outputPath = options["-o"];
            EnsureOutputIsDistinct(outputPath, [options["--rules"], options["--delta"]]);

            var policy = ReadPolicy(rulesBytes);
            var delta = ReadDelta(deltaBytes);
            var assessment = AssessmentEvaluator.Evaluate(delta, policy);
            var output = JsonSerializer.SerializeToUtf8Bytes(assessment, WebJson);
            DocumentIdentity.Verify("assessment", output);
            WriteAtomically(outputPath, output);

            return assessment.Outcome is "pass" or "warn" ? 0 : 3;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            PrintEvalUsage();
            return 2;
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine($"Invalid evaluation input: {exception.Message}");
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine($"Evaluation failed: {exception.Message}");
            return 1;
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
        {
            Console.Error.WriteLine($"Invalid evaluation input: {exception.Message}");
            return 2;
        }
    }

    private static Dictionary<string, string> ParseArguments(string[] args, params (string Name, bool Required)[] definitions)
    {
        var allowed = definitions.ToDictionary(definition => definition.Name, _ => false, StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (!allowed.ContainsKey(name)) throw new ArgumentException($"Unknown option '{name}'.");
            if (values.ContainsKey(name)) throw new ArgumentException($"Option '{name}' was supplied more than once.");
            if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
            {
                throw new ArgumentException($"Option '{name}' requires a value.");
            }

            values.Add(name, args[++index]);
        }

        foreach (var definition in definitions)
        {
            if (definition.Required && !values.ContainsKey(definition.Name))
            {
                throw new ArgumentException($"Required option '{definition.Name}' was not supplied.");
            }
        }

        return values;
    }

    private static JsonDocument ReadAndVerifyEvidence(byte[] bytes, string path)
    {
        EvidenceIdentity.Verify(bytes);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        RequireObject(root, "Evidence");
        RequireOnlyProperties(root, "schema", "id", "status", "typeId", "typeVersion", "typeResolution",
            "subject", "producer", "environment", "provenance", "payloadDigest", "payload", "artifactRefs", "message");
        RequireString(root, "schema", EvidenceSchema);
        RequireString(root, "status", "produced");
        RequireDigest(root, "id");
        RequireString(root, "typeId");
        RequireString(root, "typeVersion");
        var resolution = RequireString(root, "typeResolution");
        if (resolution is not ("recognized" or "opaque")) throw new JsonException($"Evidence '{path}' has an unknown typeResolution.");
        RequireObject(Required(root, "subject"), "Evidence subject");
        RequireString(root.GetProperty("subject"), "kind");
        RequireString(root.GetProperty("subject"), "id");
        RequireString(root.GetProperty("subject"), "commit");
        RequireOnlyProperties(root.GetProperty("subject"), "kind", "id", "commit", "repository");
        RequireObject(Required(root, "producer"), "Evidence producer");
        RequireString(root.GetProperty("producer"), "id");
        RequireString(root.GetProperty("producer"), "version");
        RequireDigest(root.GetProperty("producer"), "configDigest");
        RequireOnlyProperties(root.GetProperty("producer"), "id", "version", "configDigest");
        RequireObject(Required(root, "environment"), "Evidence environment");
        RequireString(root.GetProperty("environment"), "platform");
        RequireString(root.GetProperty("environment"), "runtime");
        RequireArrayProperty(root.GetProperty("environment"), "tools");
        RequireOnlyProperties(root.GetProperty("environment"), "platform", "runtime", "tools");
        RequireObject(Required(root, "provenance"), "Evidence provenance");
        RequireString(root.GetProperty("provenance"), "source");
        RequireArrayProperty(root.GetProperty("provenance"), "artifactDigests");
        RequireOnlyProperties(root.GetProperty("provenance"), "source", "artifactDigests");
        RequireDigest(root, "payloadDigest");
        RequireArrayProperty(root, "artifactRefs");
        return JsonDocument.Parse(bytes);
    }

    private static BaselineEvidenceReference ReadAndVerifyBaselineReference(byte[] bytes, JsonDocument baselineEvidence)
    {
        _ = CanonicalJson.Canonicalize(bytes);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        RequireObject(root, "Baseline reference");
        RequireOnlyProperties(root, "schema", "id", "role", "status", "evidenceId", "subjectCommit", "manifestDigest", "resolver", "sourceStore", "trust");
        RequireString(root, "schema", BaselineSchema);
        RequireDigest(root, "id");
        var role = RequireString(root, "role");
        if (role is not ("merge-base" or "approved-main" or "previous-release")) throw new JsonException("Baseline reference has an unknown role.");
        var status = RequireString(root, "status");
        if (status != "trusted") throw new JsonException($"Baseline status '{status}' is not trusted.");
        var evidenceId = RequireDigest(root, "evidenceId");
        var subjectCommit = RequireString(root, "subjectCommit");
        RequireDigest(root, "manifestDigest");
        RequireString(root, "sourceStore");
        var resolver = Required(root, "resolver");
        RequireObject(resolver, "Baseline resolver");
        RequireString(resolver, "id");
        RequireString(resolver, "version");
        var resolvedCommit = RequireString(resolver, "resolvedCommit");
        RequireOnlyProperties(resolver, "id", "version", "resolvedCommit", "sourceRef");
        if (!string.Equals(subjectCommit, resolvedCommit, StringComparison.Ordinal))
        {
            throw new JsonException("Baseline subjectCommit does not match the resolver's resolvedCommit.");
        }

        var trust = Required(root, "trust");
        RequireObject(trust, "Baseline trust proof");
        RequireString(trust, "issuer");
        RequireString(trust, "decisionId");
        if (trust.TryGetProperty("decisionDigest", out _)) RequireDigest(trust, "decisionDigest");
        RequireOnlyProperties(trust, "issuer", "decisionId", "decisionDigest");

        var evidenceRoot = baselineEvidence.RootElement;
        if (!string.Equals(evidenceId, RequireString(evidenceRoot, "id"), StringComparison.Ordinal))
        {
            throw new JsonException("Baseline reference evidenceId does not match the supplied baseline Evidence ID.");
        }

        var evidenceCommit = RequireString(evidenceRoot.GetProperty("subject"), "commit");
        if (!string.Equals(subjectCommit, evidenceCommit, StringComparison.Ordinal))
        {
            throw new JsonException("Baseline subjectCommit does not match the supplied baseline Evidence commit.");
        }

        var actualId = DocumentIdentity.ComputeId("baseline-reference", bytes);
        if (!string.Equals(actualId, RequireString(root, "id"), StringComparison.Ordinal))
        {
            throw new JsonException("Baseline reference id does not match its canonical identity projection.");
        }

        return new BaselineEvidenceReference(actualId, evidenceId, status);
    }

    private static NumericEvidence ToNumericEvidence(JsonElement evidence)
    {
        var payload = Required(evidence, "payload");
        RequireObject(payload, "Numeric evidence payload");
        RequireOnlyProperties(payload, "scope", "methodology", "metrics");
        var scope = RequireString(payload, "scope");
        var methodology = RequireString(payload, "methodology");
        var metrics = Required(payload, "metrics");
        RequireObject(metrics, "Numeric evidence metrics");
        return new NumericEvidence(
            RequireString(evidence, "id"),
            RequireString(evidence, "typeId"),
            RequireString(evidence, "typeVersion"),
            scope,
            methodology,
            metrics.Clone(),
            RequireString(evidence, "typeResolution"));
    }

    private static NumericPolicy ReadPolicy(byte[] bytes)
    {
        _ = CanonicalJson.Canonicalize(bytes);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        RequireObject(root, "Policy rules");
        RequireOnlyProperties(root, "id", "version", "warnAboveAbsoluteIncrease", "failAboveAbsoluteIncrease", "warnAboveRelativeIncrease", "failAboveRelativeIncrease");
        var id = RequireString(root, "id");
        var version = RequireString(root, "version");
        var warnAbsolute = OptionalPolicyDecimal(root, "warnAboveAbsoluteIncrease");
        var failAbsolute = OptionalPolicyDecimal(root, "failAboveAbsoluteIncrease");
        var warnRelative = OptionalPolicyDecimal(root, "warnAboveRelativeIncrease");
        var failRelative = OptionalPolicyDecimal(root, "failAboveRelativeIncrease");
        if (warnAbsolute is null && failAbsolute is null && warnRelative is null && failRelative is null)
        {
            throw new JsonException("Policy rules must define at least one numeric threshold.");
        }

        var canonicalRules = CanonicalJson.Canonicalize(bytes);
        var rulesDigest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(canonicalRules))}";
        return new NumericPolicy(id, version, rulesDigest, warnAbsolute, failAbsolute, warnRelative, failRelative);
    }

    private static DeltaDocument ReadDelta(byte[] bytes)
    {
        _ = CanonicalJson.Canonicalize(bytes);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        RequireObject(root, "Delta");
        RequireOnlyProperties(root, "schema", "id", "status", "typeResolution", "baselineId", "candidateEvidenceId", "comparator", "changes", "provenance", "message");
        RequireString(root, "schema", DeltaSchema);
        var id = RequireDigest(root, "id");
        var status = RequireString(root, "status");
        if (status is not ("unchanged" or "changed" or "unavailable" or "invalid" or "incompatible" or "inconclusive" or "unsupported"))
        {
            throw new JsonException($"Delta status '{status}' is unknown.");
        }

        var typeResolution = RequireString(root, "typeResolution");
        if (typeResolution is not ("recognized" or "opaque")) throw new JsonException("Delta typeResolution is unknown.");
        if (typeResolution == "opaque" && status is not ("unsupported" or "inconclusive"))
        {
            throw new JsonException("Opaque evidence cannot produce a semantic changed or unchanged Delta.");
        }

        var baselineId = RequireDigest(root, "baselineId");
        var candidateId = RequireDigest(root, "candidateEvidenceId");
        var comparatorElement = Required(root, "comparator");
        RequireObject(comparatorElement, "Delta comparator");
        RequireOnlyProperties(comparatorElement, "id", "version", "configDigest", "inputTypeId", "inputTypeVersion");
        var comparator = new ComparatorProvenance(
            RequireString(comparatorElement, "id"),
            RequireString(comparatorElement, "version"),
            RequireDigest(comparatorElement, "configDigest"),
            RequireString(comparatorElement, "inputTypeId"),
            RequireString(comparatorElement, "inputTypeVersion"));
        var changesElement = Required(root, "changes");
        RequireArray(changesElement, "Delta changes");
        var changes = changesElement.EnumerateArray().Select(ReadDeltaChange).ToArray();
        if (status == "unchanged" && changes.Length != 0) throw new JsonException("An unchanged Delta cannot contain changes.");
        if (status == "changed" && changes.Length == 0) throw new JsonException("A changed Delta must contain at least one change.");
        var provenanceElement = Required(root, "provenance");
        RequireObject(provenanceElement, "Delta provenance");
        RequireOnlyProperties(provenanceElement, "baselineEvidenceId", "candidateEvidenceId", "environment");
        var provenanceBaselineId = RequireDigest(provenanceElement, "baselineEvidenceId");
        var provenanceCandidateId = RequireDigest(provenanceElement, "candidateEvidenceId");
        if (provenanceCandidateId != candidateId) throw new JsonException("Delta provenance candidateEvidenceId does not match the document candidateEvidenceId.");
        var environmentElement = Required(provenanceElement, "environment");
        RequireObject(environmentElement, "Delta environment");
        var environment = environmentElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : throw new JsonException("Delta environment values must be strings."),
            StringComparer.Ordinal);
        var provenance = new DeltaProvenance(provenanceBaselineId, provenanceCandidateId, environment);
        var message = root.TryGetProperty("message", out var messageElement)
            ? messageElement.ValueKind == JsonValueKind.String ? messageElement.GetString() : throw new JsonException("Delta message must be a string.")
            : null;
        if ((status is "unavailable" or "invalid" or "incompatible" or "inconclusive" or "unsupported") && message is null)
        {
            throw new JsonException($"Delta status '{status}' requires a message.");
        }

        DocumentIdentity.Verify("delta", bytes);

        return new DeltaDocument(DeltaSchema, id, status, typeResolution, baselineId, candidateId, comparator, changes, provenance, message);
    }

    private static DeltaChange ReadDeltaChange(JsonElement change)
    {
        RequireObject(change, "Delta change");
        RequireOnlyProperties(change, "path", "kind", "value", "unit", "metadata");
        var path = RequireString(change, "path");
        var kind = RequireString(change, "kind");
        if (kind is not ("added" or "removed" or "modified" or "missing" or "incompatible" or "unavailable"))
        {
            throw new JsonException($"Delta change kind '{kind}' is unknown.");
        }

        var value = Required(change, "value");
        RequireObject(value, "Delta change value");
        RequireOnlyProperties(value, "before", "after", "absolute", "relative", "relativeStatus");
        var before = OptionalDeltaDecimalString(value, "before");
        var after = OptionalDeltaDecimalString(value, "after");
        var absolute = OptionalDeltaDecimalString(value, "absolute");
        var relative = OptionalDeltaDecimalString(value, "relative");
        var relativeStatus = value.TryGetProperty("relativeStatus", out var relativeStatusElement)
            ? relativeStatusElement.ValueKind == JsonValueKind.String ? relativeStatusElement.GetString() : throw new JsonException("relativeStatus must be a string.")
            : null;
        if (relativeStatus is not (null or "defined" or "undefined-zero-baseline")) throw new JsonException("relativeStatus is unknown.");
        if (relative is null && relativeStatus is not (null or "undefined-zero-baseline")) throw new JsonException("A missing relative value must state why it is undefined.");
        if (relativeStatus == "undefined-zero-baseline" &&
            (!decimal.TryParse(before, System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out var baselineValue) || baselineValue != 0))
        {
            throw new JsonException("Undefined relative change requires a zero baseline.");
        }
        var unit = change.TryGetProperty("unit", out var unitElement)
            ? unitElement.ValueKind == JsonValueKind.String ? unitElement.GetString() : throw new JsonException("Delta change unit must be a string.")
            : null;
        return new DeltaChange(path, kind, new NumericDeltaValue(before, after, absolute, relative, relativeStatus), unit);
    }

    private static string? OptionalDeltaDecimalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || !decimal.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new JsonException($"'{propertyName}' must be an invariant decimal string.");
        }

        return value.GetString();
    }

    private static decimal? OptionalPolicyDecimal(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || !decimal.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new JsonException($"'{propertyName}' must be an invariant decimal string.");
        }

        return parsed;
    }

    private static void EnsureSameSubject(JsonElement baseline, JsonElement candidate)
    {
        var baselineSubject = baseline.GetProperty("subject");
        var candidateSubject = candidate.GetProperty("subject");
        foreach (var field in new[] { "kind", "id", "repository" })
        {
            var hasBaseline = baselineSubject.TryGetProperty(field, out var left);
            var hasCandidate = candidateSubject.TryGetProperty(field, out var right);
            if (hasBaseline != hasCandidate || hasBaseline && !string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal))
            {
                throw new JsonException($"Candidate Evidence subject '{field}' does not match the baseline subject.");
            }
        }
    }

    private static byte[] ReadBounded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException($"Input file does not exist: {path}", path);
        if (info.Length > CanonicalJson.MaximumDocumentBytes) throw new JsonException($"Input '{path}' exceeds the {CanonicalJson.MaximumDocumentBytes}-byte limit.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > CanonicalJson.MaximumDocumentBytes) throw new JsonException($"Input '{path}' exceeds the {CanonicalJson.MaximumDocumentBytes}-byte limit.");
        return bytes;
    }

    private static void EnsureOutputIsDistinct(string outputPath, IEnumerable<string> inputPaths)
    {
        var fullOutput = Path.GetFullPath(outputPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (inputPaths.Any(input => string.Equals(fullOutput, Path.GetFullPath(input), comparison)))
        {
            throw new ArgumentException("Output path must not overwrite an input document.");
        }
    }

    private static void WriteAtomically(string outputPath, byte[] bytes)
    {
        var fullOutput = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullOutput)!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullOutput)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, fullOutput, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static JsonElement Required(JsonElement root, string name) => root.TryGetProperty(name, out var value)
        ? value
        : throw new JsonException($"Missing required property '{name}'.");

    private static string RequireString(JsonElement root, string name, string? expected = null)
    {
        var value = Required(root, name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new JsonException($"'{name}' must be a non-empty string.");
        }

        var actual = value.GetString()!;
        if (expected is not null && !string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new JsonException($"'{name}' must be '{expected}'.");
        }

        return actual;
    }

    private static string RequireDigest(JsonElement root, string name)
    {
        var value = RequireString(root, name);
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal) || value.AsSpan(7).IndexOfAnyExcept(NonLowercaseHexCharacters) >= 0)
        {
            throw new JsonException($"'{name}' must be a lowercase SHA-256 digest.");
        }

        return value;
    }

    private static void RequireObject(JsonElement element, string label)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException($"{label} must be an object.");
    }

    private static void RequireArrayProperty(JsonElement root, string name) => RequireArray(Required(root, name), name);

    private static void RequireArray(JsonElement element, string label)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new JsonException($"{label} must be an array.");
    }

    private static void RequireOnlyProperties(JsonElement root, params string[] allowed)
    {
        var accepted = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!accepted.Contains(property.Name)) throw new JsonException($"Unknown property '{property.Name}'.");
        }
    }

    private static void PrintDiffUsage() => Console.Error.WriteLine(
        "Usage: diffra diff --baseline-ref <baseline-reference.json> --baseline-evidence <evidence.json> --evidence <candidate.json> -o <delta.json>");

    private static void PrintEvalUsage() => Console.Error.WriteLine(
        "Usage: diffra eval --rules <rules.json> --delta <delta.json> -o <assessment.json>");
}
