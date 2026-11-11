using System.Text.Json;

namespace Diffra.Regression;

public static class JsonDiff
{
    public static string? FindFirstDifference(JsonElement expected, JsonElement actual, string jsonPointer = "")
    {
        var diffs = FindAllDifferences(expected, actual, jsonPointer);
        return diffs.Count > 0 ? diffs[0] : null;
    }

    public static List<string> FindAllDifferences(JsonElement expected, JsonElement actual, string jsonPointer = "")
    {
        var differences = new List<string>();
        CollectDifferences(expected, actual, jsonPointer, differences);
        return differences;
    }

    private static void CollectDifferences(JsonElement expected, JsonElement actual, string jsonPointer, List<string> differences)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            differences.Add(string.IsNullOrEmpty(jsonPointer) ? "/" : jsonPointer);
            return;
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var expectedProps = expected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                var actualProps = actual.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

                foreach (var (key, expVal) in expectedProps.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    var childPointer = $"{jsonPointer}/{Escape(key)}";
                    if (!actualProps.TryGetValue(key, out var actVal))
                    {
                        differences.Add(childPointer);
                    }
                    else
                    {
                        CollectDifferences(expVal, actVal, childPointer, differences);
                    }
                }

                foreach (var key in actualProps.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!expectedProps.ContainsKey(key))
                    {
                        differences.Add($"{jsonPointer}/{Escape(key)}");
                    }
                }
                break;
            }

            case JsonValueKind.Array:
            {
                var len1 = expected.GetArrayLength();
                var len2 = actual.GetArrayLength();
                var minLen = Math.Min(len1, len2);
                for (var i = 0; i < minLen; i++)
                {
                    CollectDifferences(expected[i], actual[i], $"{jsonPointer}/{i}", differences);
                }

                if (len1 != len2)
                {
                    differences.Add($"{jsonPointer}/[length:{len1}!={len2}]");
                }
                break;
            }

            case JsonValueKind.String:
                if (!string.Equals(expected.GetString(), actual.GetString(), StringComparison.Ordinal))
                {
                    differences.Add(string.IsNullOrEmpty(jsonPointer) ? "/" : jsonPointer);
                }
                break;

            case JsonValueKind.Number:
                if (expected.GetRawText() != actual.GetRawText())
                {
                    differences.Add(string.IsNullOrEmpty(jsonPointer) ? "/" : jsonPointer);
                }
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                // Type equality already checked
                break;

            default:
                differences.Add(string.IsNullOrEmpty(jsonPointer) ? "/" : jsonPointer);
                break;
        }
    }

    public static bool TryResolvePointer(JsonElement root, string jsonPath, out JsonElement element)
    {
        element = default;
        if (string.IsNullOrEmpty(jsonPath) || jsonPath == "/")
        {
            element = root;
            return true;
        }

        if (!jsonPath.StartsWith('/'))
        {
            return false;
        }

        var current = root;
        var tokens = jsonPath[1..].Split('/');

        foreach (var rawToken in tokens)
        {
            var token = Unescape(rawToken);
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(token, out var prop))
                {
                    return false;
                }
                current = prop;
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(token, out var index) || index < 0 || index >= current.GetArrayLength())
                {
                    return false;
                }
                current = current[index];
            }
            else
            {
                return false;
            }
        }

        element = current;
        return true;
    }

    private static string Escape(string name) =>
        name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static string Unescape(string token) =>
        token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
}
