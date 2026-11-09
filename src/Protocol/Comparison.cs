using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Diffra.Protocol;

/// <summary>A typed JSON observation for a structured numeric comparison.</summary>
public sealed record NumericEvidence(
    string EvidenceId,
    string TypeId,
    string TypeVersion,
    string Scope,
    string Methodology,
    JsonElement Payload,
    string TypeResolution = "recognized");

/// <summary>Resolved baseline document identity paired with its evidence identity.</summary>
public sealed record BaselineEvidenceReference(string Id, string EvidenceId, string Status);

/// <summary>Stable comparator identity and the environment used for this comparison.</summary>
public sealed record NumericComparatorOptions(
    string Id,
    string Version,
    string ConfigDigest,
    IReadOnlyDictionary<string, string> Environment)
{
    public static NumericComparatorOptions Default { get; } = new(
        "diffra.structured-numeric",
        "1.0.0",
        "sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
        new Dictionary<string, string>(StringComparer.Ordinal) { ["runtime"] = System.Environment.Version.ToString() });
}

/// <summary>Per-measurement numeric delta. A null relative value means the baseline was zero.</summary>
public sealed record NumericDeltaValue(
    string? Before,
    string? After,
    string? Absolute,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Relative,
    string? RelativeStatus);

/// <summary>One factual change emitted by a comparator, with no policy verdict.</summary>
public sealed record DeltaChange(string Path, string Kind, NumericDeltaValue Value, string? Unit);

/// <summary>Comparator identity recorded in a delta.</summary>
public sealed record ComparatorProvenance(
    string Id,
    string Version,
    string ConfigDigest,
    string InputTypeId,
    string InputTypeVersion);

/// <summary>Exact comparison inputs and execution environment.</summary>
public sealed record DeltaProvenance(
    string BaselineEvidenceId,
    string CandidateEvidenceId,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>Policy-free, typed comparison result matching the Delta v1 document shape.</summary>
public sealed record DeltaDocument(
    string Schema,
    string Id,
    string Status,
    string TypeResolution,
    string BaselineId,
    string CandidateEvidenceId,
    ComparatorProvenance Comparator,
    IReadOnlyList<DeltaChange> Changes,
    DeltaProvenance Provenance,
    string? Message);

/// <summary>Compares JSON objects whose leaves are numbers or numeric measurement objects.</summary>
public static class StructuredNumericComparator
{
    public const string DeltaSchema = "https://diffra.dev/schemas/delta-v1.schema.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// Compares numeric leaves by JSON Pointer path. Evidence type, version, scope and methodology
    /// must match exactly; malformed or unsupported value shapes return an inconclusive delta.
    /// </summary>
    public static DeltaDocument Compare(
        NumericEvidence baseline,
        NumericEvidence candidate,
        BaselineEvidenceReference baselineReference,
        NumericComparatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(baselineReference);
        options ??= NumericComparatorOptions.Default;
        ValidateOptions(options);

        var comparator = new ComparatorProvenance(
            options.Id,
            options.Version,
            options.ConfigDigest,
            candidate.TypeId,
            candidate.TypeVersion);
        var provenance = new DeltaProvenance(
            baseline.EvidenceId,
            candidate.EvidenceId,
            new SortedDictionary<string, string>(options.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal));

        if (!string.Equals(baseline.EvidenceId, baselineReference.EvidenceId, StringComparison.Ordinal))
        {
            return CreateDelta("incompatible", "recognized", baselineReference.Id, candidate.EvidenceId,
                comparator, [], provenance, "The baseline reference does not identify the supplied baseline evidence.");
        }

        if (!string.Equals(baselineReference.Status, "trusted", StringComparison.Ordinal))
        {
            return CreateDelta("inconclusive", "recognized", baselineReference.Id, candidate.EvidenceId,
                comparator, [], provenance, $"Baseline trust status '{baselineReference.Status}' is not trusted.");
        }

        if (!string.Equals(baseline.TypeId, candidate.TypeId, StringComparison.Ordinal) ||
            !string.Equals(baseline.TypeVersion, candidate.TypeVersion, StringComparison.Ordinal))
        {
            return CreateDelta("incompatible", "recognized", baselineReference.Id, candidate.EvidenceId,
                comparator, [], provenance, "Evidence type IDs and versions must match exactly.");
        }

        if (baseline.TypeResolution == "opaque" || candidate.TypeResolution == "opaque")
        {
            return CreateDelta("unsupported", "opaque", baselineReference.Id, candidate.EvidenceId,
                comparator, [], provenance, "Opaque evidence has no registered semantic comparison capability.");
        }

        if (baseline.TypeResolution != "recognized" || candidate.TypeResolution != "recognized")
        {
            return CreateDelta("inconclusive", "opaque", baselineReference.Id, candidate.EvidenceId,
                comparator, [], provenance, "Evidence type resolution state is not recognized.");
        }

        if (!string.Equals(baseline.Scope, candidate.Scope, StringComparison.Ordinal) ||
            !string.Equals(baseline.Methodology, candidate.Methodology, StringComparison.Ordinal))
        {
            return CreateDelta("incompatible", "recognized", baselineReference.Id, candidate.EvidenceId,
                comparator, [], provenance, "Evidence scope and methodology must match exactly.");
        }

        if (string.IsNullOrWhiteSpace(candidate.TypeId) || string.IsNullOrWhiteSpace(candidate.TypeVersion) ||
            string.IsNullOrWhiteSpace(candidate.Scope) || string.IsNullOrWhiteSpace(candidate.Methodology))
        {
            return CreateDelta("inconclusive", "opaque", baselineReference.Id, candidate.EvidenceId,
                comparator, [], provenance, "Typed evidence is missing its type, scope, or methodology identity.");
        }

        var baselineValid = TryReadMetrics(baseline.Payload, out var before, out var baselineError);
        var candidateValid = TryReadMetrics(candidate.Payload, out var after, out var candidateError);
        if (!baselineValid || !candidateValid)
        {
            var message = baselineError ?? candidateError ?? "Evidence payload could not be interpreted as numeric data.";
            return CreateDelta("inconclusive", "recognized", baselineReference.Id, candidate.EvidenceId,
                comparator, [], provenance, message);
        }

        var changes = new List<DeltaChange>();
        foreach (var path in before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var hasBefore = before.TryGetValue(path, out var oldValue);
            var hasAfter = after.TryGetValue(path, out var newValue);
            if (!hasBefore)
            {
                changes.Add(new DeltaChange(path, "added", new NumericDeltaValue(null, Format(newValue!.Value), null, null, null), newValue.Unit));
                continue;
            }

            if (!hasAfter)
            {
                changes.Add(new DeltaChange(path, "removed", new NumericDeltaValue(Format(oldValue!.Value), null, null, null, null), oldValue.Unit));
                continue;
            }

            if (!string.Equals(oldValue!.Unit, newValue!.Unit, StringComparison.Ordinal))
            {
                return CreateDelta("incompatible", "recognized", baselineReference.Id, candidate.EvidenceId,
                    comparator, [], provenance, $"Measurement unit changed at '{path}'.");
            }

            if (oldValue.Value == newValue.Value)
            {
                continue;
            }

            decimal absolute;
            decimal? relative;
            try
            {
                absolute = checked(newValue.Value - oldValue.Value);
                relative = oldValue.Value == 0 ? null : absolute / oldValue.Value;
            }
            catch (OverflowException)
            {
                return CreateDelta("inconclusive", "recognized", baselineReference.Id, candidate.EvidenceId,
                    comparator, [], provenance, $"Numeric delta overflowed at '{path}'.");
            }

            changes.Add(new DeltaChange(
                path,
                "modified",
                new NumericDeltaValue(
                    Format(oldValue.Value),
                    Format(newValue.Value),
                    Format(absolute),
                    relative is null ? null : Format(relative.Value),
                    relative is null ? "undefined-zero-baseline" : "defined"),
                oldValue.Unit));
        }

        return CreateDelta(
            changes.Count == 0 ? "unchanged" : "changed",
            "recognized",
            baselineReference.Id,
            candidate.EvidenceId,
            comparator,
            changes,
            provenance,
            null);
    }

    private static bool TryReadMetrics(JsonElement payload, out SortedDictionary<string, NumericMetric> metrics, out string? error)
    {
        metrics = new SortedDictionary<string, NumericMetric>(StringComparer.Ordinal);
        error = null;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "Numeric evidence payload must be a JSON object.";
            return false;
        }

        try
        {
            _ = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(payload.GetRawText()));
        }
        catch (JsonException exception)
        {
            error = $"Evidence payload is outside canonical JSON v1: {exception.Message}";
            return false;
        }

        return VisitObject(payload, string.Empty, metrics, out error);
    }

    private static bool VisitObject(JsonElement value, string path, IDictionary<string, NumericMetric> metrics, out string? error)
    {
        error = null;
        foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var childPath = $"{path}/{EscapePointer(property.Name)}";
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                if (property.Value.TryGetProperty("value", out var measurementValue))
                {
                    if (!TryReadNumber(measurementValue, out var number))
                    {
                        error = $"Measurement at '{childPath}' must have a finite decimal value.";
                        return false;
                    }

                    string? unit = null;
                    var valid = true;
                    foreach (var member in property.Value.EnumerateObject())
                    {
                        if (member.NameEquals("value")) continue;
                        if (member.NameEquals("unit") && member.Value.ValueKind == JsonValueKind.String)
                        {
                            unit = member.Value.GetString();
                            continue;
                        }

                        valid = false;
                        break;
                    }

                    if (!valid || unit is { Length: 0 })
                    {
                        error = $"Measurement at '{childPath}' may contain only numeric 'value' and string 'unit'.";
                        return false;
                    }

                    metrics.Add(childPath, new NumericMetric(number, unit));
                }
                else if (!VisitObject(property.Value, childPath, metrics, out error))
                {
                    return false;
                }

                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDecimal(out var scalar))
            {
                error = $"Value at '{childPath}' is not a supported finite numeric measurement.";
                return false;
            }

            metrics.Add(childPath, new NumericMetric(scalar, null));
        }

        return true;
    }

    private static string EscapePointer(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static bool TryReadNumber(JsonElement value, out decimal number)
    {
        number = default;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetDecimal(out number);
        return value.ValueKind == JsonValueKind.String && decimal.TryParse(
            value.GetString(),
            System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint,
            System.Globalization.CultureInfo.InvariantCulture,
            out number);
    }

    internal static string Format(decimal value) => value.ToString("G29", System.Globalization.CultureInfo.InvariantCulture);

    private static void ValidateOptions(NumericComparatorOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Version);
        if (!IsDigest(options.ConfigDigest)) throw new ArgumentException("ConfigDigest must be a SHA-256 digest.", nameof(options));
        ArgumentNullException.ThrowIfNull(options.Environment);
    }

    private static bool IsDigest(string value) => value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) && value[7..].All(Uri.IsHexDigit);

    private static DeltaDocument CreateDelta(
        string status,
        string typeResolution,
        string baselineId,
        string candidateEvidenceId,
        ComparatorProvenance comparator,
        IReadOnlyList<DeltaChange> changes,
        DeltaProvenance provenance,
        string? message)
    {
        var projection = new
        {
            schema = DeltaSchema,
            status,
            typeResolution,
            baselineId,
            candidateEvidenceId,
            comparator,
            changes,
            provenance,
            message
        };
        var projectionBytes = JsonSerializer.SerializeToUtf8Bytes(projection, SerializerOptions);
        var canonical = CanonicalJson.Canonicalize(projectionBytes);
        var framed = Encoding.UTF8.GetBytes("diffra/v1\0delta\0").Concat(canonical).ToArray();
        var id = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(framed))}";
        return new DeltaDocument(DeltaSchema, id, status, typeResolution, baselineId, candidateEvidenceId, comparator, changes, provenance, message);
    }

    private sealed record NumericMetric(decimal Value, string? Unit);
}
