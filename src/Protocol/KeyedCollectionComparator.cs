using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Diffra.Protocol;

public enum ArrayComparisonMode
{
    OrderedSequence,
    KeyedCollection,
}

public sealed record KeyedCollectionOptions(
    ArrayComparisonMode ArrayMode = ArrayComparisonMode.KeyedCollection,
    string KeyProperty = "id",
    IReadOnlySet<string>? ExcludedPaths = null);

public sealed record StructuralChange(
    string Path,
    string Kind, // added, removed, modified, type_changed
    JsonNode? Before,
    JsonNode? After,
    string? Description = null);

/// <summary>Compares structured JSON documents with distinct semantics for ordered vs keyed arrays and null/missing types.</summary>
public static class KeyedCollectionComparator
{
    public static IReadOnlyList<StructuralChange> Compare(
        JsonElement baseline,
        JsonElement candidate,
        KeyedCollectionOptions? options = null)
    {
        options ??= new KeyedCollectionOptions();
        var changes = new List<StructuralChange>();
        CompareElements(baseline, candidate, string.Empty, options, changes);
        return changes;
    }

    private static void CompareElements(
        JsonElement? baseline,
        JsonElement? candidate,
        string path,
        KeyedCollectionOptions options,
        List<StructuralChange> changes)
    {
        if (options.ExcludedPaths != null && options.ExcludedPaths.Contains(path))
        {
            return;
        }

        // Case 1: Baseline missing (added in candidate)
        if (baseline == null || baseline.Value.ValueKind == JsonValueKind.Undefined)
        {
            if (candidate != null && candidate.Value.ValueKind != JsonValueKind.Undefined)
            {
                changes.Add(new StructuralChange(
                    Path: string.IsNullOrEmpty(path) ? "/" : path,
                    Kind: "added",
                    Before: null,
                    After: JsonNode.Parse(candidate.Value.GetRawText())));
            }
            return;
        }

        // Case 2: Candidate missing (removed from baseline)
        if (candidate == null || candidate.Value.ValueKind == JsonValueKind.Undefined)
        {
            changes.Add(new StructuralChange(
                Path: string.IsNullOrEmpty(path) ? "/" : path,
                Kind: "removed",
                Before: JsonNode.Parse(baseline.Value.GetRawText()),
                After: null));
            return;
        }

        var bElem = baseline.Value;
        var cElem = candidate.Value;

        // Case 3: Type change (e.g. number to string, object to array, null to string)
        if (bElem.ValueKind != cElem.ValueKind)
        {
            changes.Add(new StructuralChange(
                Path: string.IsNullOrEmpty(path) ? "/" : path,
                Kind: "type_changed",
                Before: JsonNode.Parse(bElem.GetRawText()),
                After: JsonNode.Parse(cElem.GetRawText()),
                Description: $"Type changed from {bElem.ValueKind} to {cElem.ValueKind}."));
            return;
        }

        // Case 4: Objects
        if (bElem.ValueKind == JsonValueKind.Object)
        {
            var bProps = bElem.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
            var cProps = cElem.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

            var allKeys = bProps.Keys.Union(cProps.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal);
            foreach (var key in allKeys)
            {
                var childPath = $"{path}/{key}";
                bool inBase = bProps.TryGetValue(key, out var bVal);
                bool inCand = cProps.TryGetValue(key, out var cVal);

                if (inBase && !inCand)
                {
                    CompareElements(bVal, null, childPath, options, changes);
                }
                else if (!inBase && inCand)
                {
                    CompareElements(null, cVal, childPath, options, changes);
                }
                else
                {
                    CompareElements(bVal, cVal, childPath, options, changes);
                }
            }
            return;
        }

        // Case 5: Arrays
        if (bElem.ValueKind == JsonValueKind.Array)
        {
            if (options.ArrayMode == ArrayComparisonMode.KeyedCollection)
            {
                CompareKeyedArrays(bElem, cElem, path, options, changes);
            }
            else
            {
                CompareOrderedArrays(bElem, cElem, path, options, changes);
            }
            return;
        }

        // Case 6: Primitive values, compared by decoded value so formatting-only differences are unchanged
        if (!PrimitiveValuesEqual(bElem, cElem))
        {
            changes.Add(new StructuralChange(
                Path: string.IsNullOrEmpty(path) ? "/" : path,
                Kind: "modified",
                Before: JsonNode.Parse(bElem.GetRawText()),
                After: JsonNode.Parse(cElem.GetRawText())));
        }
    }

    /// <summary>Compares two primitive elements of the same kind by decoded value.</summary>
    private static bool PrimitiveValuesEqual(JsonElement baseline, JsonElement candidate)
    {
        switch (baseline.ValueKind)
        {
            case JsonValueKind.String:
                return string.Equals(baseline.GetString(), candidate.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return NumbersEqual(baseline.GetRawText(), candidate.GetRawText());
            default:
                // True, False and Null carry no value beyond their kind, which already matches.
                return true;
        }
    }

    /// <summary>
    /// Compares two JSON number tokens for exact numeric equality, without the rounding of a binary or decimal conversion.
    /// </summary>
    private static bool NumbersEqual(string baselineToken, string candidateToken)
    {
        if (TryNormalizeNumber(baselineToken, out var baselineNumber) &&
            TryNormalizeNumber(candidateToken, out var candidateNumber))
        {
            return baselineNumber.Equals(candidateNumber);
        }

        // An exponent too large to normalize: fall back to comparing the literal tokens.
        return string.Equals(baselineToken, candidateToken, StringComparison.Ordinal);
    }

    /// <summary>
    /// Normalizes a JSON number token to (sign, significant digits, decimal exponent), so that two tokens are
    /// numerically equal exactly when their normalized forms are equal. Zero has a single form regardless of sign
    /// or exponent.
    /// </summary>
    private static bool TryNormalizeNumber(string token, out NormalizedNumber number)
    {
        number = default;
        var text = token.AsSpan();

        var negative = text.Length > 0 && text[0] == '-';
        if (negative)
        {
            text = text[1..];
        }

        long exponent = 0;
        var exponentIndex = text.IndexOfAny('e', 'E');
        if (exponentIndex >= 0)
        {
            if (!long.TryParse(text[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
            {
                return false;
            }

            // Keep headroom so shifting by the digit count below cannot overflow.
            if (exponent is > long.MaxValue / 2 or < long.MinValue / 2)
            {
                return false;
            }

            text = text[..exponentIndex];
        }

        string digits;
        var pointIndex = text.IndexOf('.');
        if (pointIndex >= 0)
        {
            var fraction = text[(pointIndex + 1)..];
            digits = string.Concat(text[..pointIndex], fraction);
            exponent -= fraction.Length;
        }
        else
        {
            digits = text.ToString();
        }

        digits = digits.TrimStart('0');
        if (digits.Length == 0)
        {
            number = new NormalizedNumber(Negative: false, Digits: "0", Exponent: 0);
            return true;
        }

        var trimmed = digits.TrimEnd('0');
        exponent += digits.Length - trimmed.Length;
        number = new NormalizedNumber(negative, trimmed, exponent);
        return true;
    }

    private readonly record struct NormalizedNumber(bool Negative, string Digits, long Exponent);

    private static void CompareKeyedArrays(
        JsonElement baseline,
        JsonElement candidate,
        string path,
        KeyedCollectionOptions options,
        List<StructuralChange> changes)
    {
        var bList = baseline.EnumerateArray().ToList();
        var cList = candidate.EnumerateArray().ToList();

        // Check if all elements are objects containing the key property
        bool bAllKeyed = bList.All(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(options.KeyProperty, out _));
        bool cAllKeyed = cList.All(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(options.KeyProperty, out _));

        if (!bAllKeyed || !cAllKeyed)
        {
            // Fallback to ordered comparison if not cleanly keyed
            CompareOrderedArrays(baseline, candidate, path, options, changes);
            return;
        }

        var bDict = bList.ToDictionary(e => e.GetProperty(options.KeyProperty).GetString()!, e => e, StringComparer.Ordinal);
        var cDict = cList.ToDictionary(e => e.GetProperty(options.KeyProperty).GetString()!, e => e, StringComparer.Ordinal);

        var allKeys = bDict.Keys.Union(cDict.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal);

        foreach (var key in allKeys)
        {
            var itemPath = $"{path}[{options.KeyProperty}={key}]";
            bool inBase = bDict.TryGetValue(key, out var bItem);
            bool inCand = cDict.TryGetValue(key, out var cItem);

            if (inBase && !inCand)
            {
                changes.Add(new StructuralChange(itemPath, "removed", JsonNode.Parse(bItem.GetRawText()), null));
            }
            else if (!inBase && inCand)
            {
                changes.Add(new StructuralChange(itemPath, "added", null, JsonNode.Parse(cItem.GetRawText())));
            }
            else
            {
                CompareElements(bItem, cItem, itemPath, options, changes);
            }
        }
    }

    private static void CompareOrderedArrays(
        JsonElement baseline,
        JsonElement candidate,
        string path,
        KeyedCollectionOptions options,
        List<StructuralChange> changes)
    {
        var bList = baseline.EnumerateArray().ToList();
        var cList = candidate.EnumerateArray().ToList();

        int max = Math.Max(bList.Count, cList.Count);
        for (int i = 0; i < max; i++)
        {
            var idxPath = $"{path}[{i}]";
            if (i >= bList.Count)
            {
                changes.Add(new StructuralChange(idxPath, "added", null, JsonNode.Parse(cList[i].GetRawText())));
            }
            else if (i >= cList.Count)
            {
                changes.Add(new StructuralChange(idxPath, "removed", JsonNode.Parse(bList[i].GetRawText()), null));
            }
            else
            {
                CompareElements(bList[i], cList[i], idxPath, options, changes);
            }
        }
    }
}
