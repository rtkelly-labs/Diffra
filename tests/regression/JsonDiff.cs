using System.Text.Json;

namespace Diffra.Regression;

public static class JsonDiff
{
    public static string? FindFirstDifference(JsonElement expected, JsonElement actual, string jsonPointer = "")
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            return string.IsNullOrEmpty(jsonPointer) ? "/" : jsonPointer;
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
                        return childPointer;
                    }

                    var diff = FindFirstDifference(expVal, actVal, childPointer);
                    if (diff is not null)
                    {
                        return diff;
                    }
                }

                foreach (var key in actualProps.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!expectedProps.ContainsKey(key))
                    {
                        return $"{jsonPointer}/{Escape(key)}";
                    }
                }

                return null;
            }

            case JsonValueKind.Array:
            {
                var len1 = expected.GetArrayLength();
                var len2 = actual.GetArrayLength();
                var minLen = Math.Min(len1, len2);
                for (var i = 0; i < minLen; i++)
                {
                    var diff = FindFirstDifference(expected[i], actual[i], $"{jsonPointer}/{i}");
                    if (diff is not null)
                    {
                        return diff;
                    }
                }

                if (len1 != len2)
                {
                    return string.IsNullOrEmpty(jsonPointer) ? "/" : jsonPointer;
                }

                return null;
            }

            case JsonValueKind.String:
                return string.Equals(expected.GetString(), actual.GetString(), StringComparison.Ordinal)
                    ? null
                    : (string.IsNullOrEmpty(jsonPointer) ? "/" : jsonPointer);

            case JsonValueKind.Number:
                return expected.GetRawText() == actual.GetRawText()
                    ? null
                    : (string.IsNullOrEmpty(jsonPointer) ? "/" : jsonPointer);

            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return null;

            default:
                return string.IsNullOrEmpty(jsonPointer) ? "/" : jsonPointer;
        }
    }

    private static string Escape(string name) =>
        name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
