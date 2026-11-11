using System.Text.Json;
using System.Text.RegularExpressions;

namespace Diffra.Regression;

public sealed record DeclaredBreakRule(
    string DocumentKind,
    string JsonPath,
    string Reason,
    Func<JsonElement, JsonElement, bool>? Validator = null);

public static class DeclaredBreakAllowList
{
    private static readonly List<DeclaredBreakRule> Rules =
    [
        // PR #115: Keep host runtime out of Delta identity
        new DeclaredBreakRule(
            DocumentKind: "delta",
            JsonPath: "/provenance/environment/runtime",
            Reason: "DECLARED-BREAK delta-identity-runtime (PR #115)",
            Validator: (expected, actual) =>
                expected.ValueKind == JsonValueKind.String &&
                IsPatchVersionRuntimeString(expected.GetString()) &&
                actual.ValueKind == JsonValueKind.String &&
                actual.GetString() == "dotnet"
        ),
        new DeclaredBreakRule(
            DocumentKind: "delta",
            JsonPath: "/id",
            Reason: "DECLARED-BREAK delta-identity-runtime (PR #115)",
            Validator: (expected, actual) =>
                expected.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(expected.GetString()) &&
                actual.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(actual.GetString())
        ),

        // Evidence host environment variance for collect cases across platforms/runtimes
        new DeclaredBreakRule(
            DocumentKind: "evidence",
            JsonPath: "/environment/platform",
            Reason: "ENVIRONMENT-PLATFORM host-platform-drift (diffra collect records host OS)",
            Validator: (expected, actual) =>
                expected.ValueKind == JsonValueKind.String &&
                actual.ValueKind == JsonValueKind.String
        ),
        new DeclaredBreakRule(
            DocumentKind: "evidence",
            JsonPath: "/environment/runtime",
            Reason: "ENVIRONMENT-RUNTIME host-runtime-drift (diffra collect records host framework)",
            Validator: (expected, actual) =>
                expected.ValueKind == JsonValueKind.String &&
                actual.ValueKind == JsonValueKind.String
        ),
        new DeclaredBreakRule(
            DocumentKind: "evidence",
            JsonPath: "/environment/tools/0/version",
            Reason: "ENVIRONMENT-TOOLS host-sdk-drift (diffra collect records host SDK version)",
            Validator: (expected, actual) =>
                expected.ValueKind == JsonValueKind.String &&
                actual.ValueKind == JsonValueKind.String
        ),
        new DeclaredBreakRule(
            DocumentKind: "evidence",
            JsonPath: "/environment/tools",
            Reason: "ENVIRONMENT-TOOLS host-sdk-drift (diffra collect records host SDK tool inventory)",
            Validator: (expected, actual) =>
                expected.ValueKind == JsonValueKind.Array &&
                actual.ValueKind == JsonValueKind.Array
        ),
        new DeclaredBreakRule(
            DocumentKind: "evidence",
            JsonPath: "/id",
            Reason: "IDENTITY-UNVERIFIED evidence-environment-drift (diffra collect incorporates host environment in Evidence ID)",
            Validator: (expected, actual) =>
                expected.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(expected.GetString()) &&
                actual.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(actual.GetString())
        ),
    ];

    public static bool TryMatch(
        string documentKind,
        string jsonPath,
        JsonElement expectedRoot,
        JsonElement actualRoot,
        out string reason)
    {
        reason = "";

        foreach (var rule in Rules)
        {
            if (!string.Equals(rule.DocumentKind, documentKind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(rule.JsonPath, jsonPath, StringComparison.Ordinal))
            {
                continue;
            }

            if (rule.Validator != null)
            {
                var hasExp = JsonDiff.TryResolvePointer(expectedRoot, jsonPath, out var expVal);
                var hasAct = JsonDiff.TryResolvePointer(actualRoot, jsonPath, out var actVal);

                if (!hasExp || !hasAct || !rule.Validator(expVal, actVal))
                {
                    return false;
                }
            }

            reason = rule.Reason;
            return true;
        }

        return false;
    }

    public static bool IsPatchVersionRuntimeString(string? runtime)
    {
        if (string.IsNullOrWhiteSpace(runtime))
        {
            return false;
        }

        var trimmed = runtime.Trim();
        if (trimmed.StartsWith(".NET ", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[".NET ".Length..].Trim();
        }

        if (Version.TryParse(trimmed, out var v) && v.Build >= 0)
        {
            return true;
        }

        return Regex.IsMatch(trimmed, @"^\d+\.\d+\.\d+");
    }

    public static bool IsExactInvariantDeltaEnvironment(JsonElement envElement)
    {
        if (envElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var props = envElement.EnumerateObject().ToList();
        return props.Count == 1 &&
               string.Equals(props[0].Name, "runtime", StringComparison.Ordinal) &&
               props[0].Value.ValueKind == JsonValueKind.String &&
               string.Equals(props[0].Value.GetString(), "dotnet", StringComparison.Ordinal);
    }
}
