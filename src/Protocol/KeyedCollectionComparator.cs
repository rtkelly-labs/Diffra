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

        // Case 6: Primitive values
        var bRaw = bElem.GetRawText();
        var cRaw = cElem.GetRawText();
        if (!string.Equals(bRaw, cRaw, StringComparison.Ordinal))
        {
            changes.Add(new StructuralChange(
                Path: string.IsNullOrEmpty(path) ? "/" : path,
                Kind: "modified",
                Before: JsonNode.Parse(bRaw),
                After: JsonNode.Parse(cRaw)));
        }
    }

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
