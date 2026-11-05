using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Diffra.Protocol;

/// <summary>One member in a typed public API surface catalogue.</summary>
public sealed record ApiSurfaceMember(
    string Id,
    string Kind,
    string Signature,
    string? ReturnType = null,
    IReadOnlyList<string>? Parameters = null,
    bool IsStatic = false,
    bool IsAsync = false);

/// <summary>Typed payload representing an exported public API surface.</summary>
public sealed record ApiSurfacePayload(
    string Scope,
    string Methodology,
    IReadOnlyList<ApiSurfaceMember> Members)
{
    public const string SchemaUri = "https://diffra.dev/schemas/api-surface-v1.schema.json";

    public JsonObject ToJsonObject()
    {
        var membersArray = new JsonArray();
        foreach (var m in Members.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var node = new JsonObject
            {
                ["id"] = m.Id,
                ["kind"] = m.Kind,
                ["signature"] = m.Signature,
                ["returnType"] = m.ReturnType,
                ["isStatic"] = m.IsStatic,
                ["isAsync"] = m.IsAsync,
            };

            if (m.Parameters != null)
            {
                var paramsArray = new JsonArray();
                foreach (var p in m.Parameters)
                {
                    paramsArray.Add(p);
                }
                node["parameters"] = paramsArray;
            }
            else
            {
                node["parameters"] = null;
            }

            membersArray.Add(node);
        }

        return new JsonObject
        {
            ["schema"] = SchemaUri,
            ["scope"] = Scope,
            ["methodology"] = Methodology,
            ["members"] = membersArray,
        };
    }

    public static ApiSurfacePayload FromJsonElement(JsonElement root)
    {
        var scope = root.GetProperty("scope").GetString()!;
        var methodology = root.GetProperty("methodology").GetString()!;

        var members = new List<ApiSurfaceMember>();
        if (root.TryGetProperty("members", out var membersElem) && membersElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in membersElem.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                var kind = item.GetProperty("kind").GetString()!;
                var sig = item.GetProperty("signature").GetString()!;
                var returnType = item.TryGetProperty("returnType", out var rt) && rt.ValueKind == JsonValueKind.String ? rt.GetString() : null;
                var isStatic = item.TryGetProperty("isStatic", out var isStat) && isStat.GetBoolean();
                var isAsync = item.TryGetProperty("isAsync", out var isAsy) && isAsy.GetBoolean();

                List<string>? parameters = null;
                if (item.TryGetProperty("parameters", out var pElem) && pElem.ValueKind == JsonValueKind.Array)
                {
                    parameters = new List<string>();
                    foreach (var p in pElem.EnumerateArray())
                    {
                        if (p.ValueKind == JsonValueKind.String) parameters.Add(p.GetString()!);
                    }
                }

                members.Add(new ApiSurfaceMember(id, kind, sig, returnType, parameters, isStatic, isAsync));
            }
        }

        return new ApiSurfacePayload(scope, methodology, members);
    }
}

/// <summary>Semantic delta between two API surfaces.</summary>
public sealed record ApiSurfaceDelta(
    IReadOnlyList<ApiSurfaceMember> AddedMembers,
    IReadOnlyList<ApiSurfaceMember> RemovedMembers,
    IReadOnlyList<ApiSurfaceModification> ModifiedMembers,
    int UnchangedCount)
{
    public bool HasBreakingChanges => RemovedMembers.Count > 0 || ModifiedMembers.Count > 0;
}

public sealed record ApiSurfaceModification(
    string MemberId,
    ApiSurfaceMember Before,
    ApiSurfaceMember After,
    string DifferenceDescription);

/// <summary>Policy enforcing API backwards-compatibility.</summary>
public sealed record ApiSurfacePolicy(
    bool DisallowBreakingChanges = true,
    bool DisallowRemovals = true,
    bool DisallowModifications = true);

/// <summary>Compares and evaluates typed API surfaces.</summary>
public static class ApiSurfaceEvaluator
{
    public static ApiSurfaceDelta Compare(ApiSurfacePayload baseline, ApiSurfacePayload candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        var baseMap = baseline.Members.ToDictionary(m => m.Id, m => m, StringComparer.Ordinal);
        var candMap = candidate.Members.ToDictionary(m => m.Id, m => m, StringComparer.Ordinal);

        var added = new List<ApiSurfaceMember>();
        var removed = new List<ApiSurfaceMember>();
        var modified = new List<ApiSurfaceModification>();
        int unchanged = 0;

        foreach (var (id, candMember) in candMap)
        {
            if (baseMap.TryGetValue(id, out var baseMember))
            {
                if (!string.Equals(candMember.Signature, baseMember.Signature, StringComparison.Ordinal) ||
                    !string.Equals(candMember.ReturnType, baseMember.ReturnType, StringComparison.Ordinal))
                {
                    modified.Add(new ApiSurfaceModification(
                        id,
                        baseMember,
                        candMember,
                        $"Signature changed from '{baseMember.Signature}' to '{candMember.Signature}'."));
                }
                else
                {
                    unchanged++;
                }
            }
            else
            {
                added.Add(candMember);
            }
        }

        foreach (var (id, baseMember) in baseMap)
        {
            if (!candMap.ContainsKey(id))
            {
                removed.Add(baseMember);
            }
        }

        return new ApiSurfaceDelta(added, removed, modified, unchanged);
    }

    public static (string Outcome, IReadOnlyList<AssessmentFinding> Findings) Evaluate(
        ApiSurfacePayload candidate,
        ApiSurfaceDelta delta,
        ApiSurfacePolicy policy)
    {
        var findings = new List<AssessmentFinding>();
        var outcome = "pass";

        if (policy.DisallowRemovals && delta.RemovedMembers.Count > 0)
        {
            outcome = "fail";
            foreach (var rem in delta.RemovedMembers)
            {
                findings.Add(new AssessmentFinding(
                    "api/member-removed",
                    "fail",
                    $"Public API member '{rem.Id}' was removed, introducing a breaking change.",
                    [rem.Id],
                    null,
                    null));
            }
        }

        if (policy.DisallowModifications && delta.ModifiedMembers.Count > 0)
        {
            outcome = "fail";
            foreach (var mod in delta.ModifiedMembers)
            {
                findings.Add(new AssessmentFinding(
                    "api/member-modified",
                    "fail",
                    $"Public API member '{mod.MemberId}' signature was modified: {mod.DifferenceDescription}",
                    [mod.MemberId],
                    null,
                    null));
            }
        }

        if (delta.AddedMembers.Count > 0)
        {
            foreach (var add in delta.AddedMembers)
            {
                findings.Add(new AssessmentFinding(
                    "api/member-added",
                    "pass",
                    $"New non-breaking API member '{add.Id}' introduced.",
                    [add.Id],
                    null,
                    null));
            }
        }

        if (findings.Count == 0)
        {
            findings.Add(new AssessmentFinding(
                "api/backwards-compatible",
                "pass",
                $"API surface is 100% backwards-compatible ({delta.UnchangedCount} members unchanged).",
                [],
                delta.UnchangedCount,
                null));
        }

        return (outcome, findings);
    }
}

/// <summary>Payload representing emitted source text artifact metadata.</summary>
public sealed record SourceTextPayload(
    string Scope,
    string Language,
    int LineCount,
    int CharacterCount,
    string ContentDigest,
    string? RawSourcePath = null)
{
    public const string SchemaUri = "https://diffra.dev/schemas/source-text-v1.schema.json";

    public JsonObject ToJsonObject() => new()
    {
        ["schema"] = SchemaUri,
        ["scope"] = Scope,
        ["language"] = Language,
        ["lineCount"] = LineCount,
        ["characterCount"] = CharacterCount,
        ["contentDigest"] = ContentDigest,
        ["rawSourcePath"] = RawSourcePath,
    };

    public static SourceTextPayload FromJsonElement(JsonElement root) => new(
        Scope: root.GetProperty("scope").GetString()!,
        Language: root.GetProperty("language").GetString()!,
        LineCount: root.GetProperty("lineCount").GetInt32(),
        CharacterCount: root.GetProperty("characterCount").GetInt32(),
        ContentDigest: root.GetProperty("contentDigest").GetString()!,
        RawSourcePath: root.TryGetProperty("rawSourcePath", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null);
}

/// <summary>Semantic delta between two source text artifacts.</summary>
public sealed record SourceTextDelta(
    int LineCountDelta,
    int CharacterCountDelta,
    bool IsContentIdentical,
    string BaselineDigest,
    string CandidateDigest);

public static class SourceTextEvaluator
{
    public static SourceTextDelta Compare(SourceTextPayload baseline, SourceTextPayload candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        var linesDelta = candidate.LineCount - baseline.LineCount;
        var charsDelta = candidate.CharacterCount - baseline.CharacterCount;
        var identical = string.Equals(baseline.ContentDigest, candidate.ContentDigest, StringComparison.Ordinal);

        return new SourceTextDelta(linesDelta, charsDelta, identical, baseline.ContentDigest, candidate.ContentDigest);
    }
}
