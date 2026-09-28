using System.Text.Json;
using System.Text.Json.Nodes;

namespace Diffra.Protocol;

/// <summary>Represents a CloudEvents v1.0.2 event envelope wrapping Diffra documents.</summary>
public sealed record DiffraCloudEvent(
    string Id,
    string Source,
    string Type,
    DateTimeOffset Time,
    string DataSchema,
    JsonObject Data,
    string? Subject = null,
    string? DiffraOutcome = null,
    string? DiffraDeltaStatus = null,
    string? DiffraGitHash = null,
    string? DiffraGitBranch = null,
    int? DiffraGitHubPr = null,
    string? TraceParent = null,
    string? TraceState = null)
{
    public const string SpecVersion = "1.0";
    public const string ContentType = "application/json";

    public const string EvidenceEventType = "dev.diffra.evidence.v1";
    public const string DeltaEventType = "dev.diffra.delta.v1";
    public const string AssessmentEventType = "dev.diffra.assessment.v1";

    public JsonObject ToJsonObject()
    {
        var root = new JsonObject
        {
            ["specversion"] = SpecVersion,
            ["id"] = Id,
            ["source"] = Source,
            ["type"] = Type,
            ["time"] = Time.ToString("O"),
            ["datacontenttype"] = ContentType,
            ["dataschema"] = DataSchema,
            ["data"] = Data.DeepClone(),
        };

        if (!string.IsNullOrEmpty(Subject)) root["subject"] = Subject;
        if (!string.IsNullOrEmpty(DiffraOutcome)) root["diffraoutcome"] = DiffraOutcome;
        if (!string.IsNullOrEmpty(DiffraDeltaStatus)) root["diffradeltastatus"] = DiffraDeltaStatus;
        if (!string.IsNullOrEmpty(DiffraGitHash)) root["diffragithash"] = DiffraGitHash;
        if (!string.IsNullOrEmpty(DiffraGitBranch)) root["diffragitbranch"] = DiffraGitBranch;
        if (DiffraGitHubPr.HasValue) root["diffragithubpr"] = DiffraGitHubPr.Value;
        if (!string.IsNullOrEmpty(TraceParent)) root["traceparent"] = TraceParent;
        if (!string.IsNullOrEmpty(TraceState)) root["tracestate"] = TraceState;

        return root;
    }

    public static DiffraCloudEvent FromJsonElement(JsonElement root)
    {
        var specVersion = root.GetProperty("specversion").GetString();
        if (specVersion != SpecVersion)
        {
            throw new JsonException($"Unsupported CloudEvents specversion: {specVersion}. Expected {SpecVersion}.");
        }

        var id = root.GetProperty("id").GetString()!;
        var source = root.GetProperty("source").GetString()!;
        var type = root.GetProperty("type").GetString()!;
        var time = root.GetProperty("time").GetDateTimeOffset();
        var dataSchema = root.GetProperty("dataschema").GetString()!;
        var data = JsonNode.Parse(root.GetProperty("data").GetRawText())!.AsObject();

        string? subject = root.TryGetProperty("subject", out var s) ? s.GetString() : null;
        string? outcome = root.TryGetProperty("diffraoutcome", out var o) ? o.GetString() : null;
        string? deltaStatus = root.TryGetProperty("diffradeltastatus", out var ds) ? ds.GetString() : null;
        string? gitHash = root.TryGetProperty("diffragithash", out var gh) ? gh.GetString() : null;
        string? gitBranch = root.TryGetProperty("diffragitbranch", out var gb) ? gb.GetString() : null;
        int? pr = root.TryGetProperty("diffragithubpr", out var p) ? p.GetInt32() : null;
        string? traceParent = root.TryGetProperty("traceparent", out var tp) ? tp.GetString() : null;
        string? traceState = root.TryGetProperty("tracestate", out var ts) ? ts.GetString() : null;

        return new DiffraCloudEvent(
            id, source, type, time, dataSchema, data,
            subject, outcome, deltaStatus, gitHash, gitBranch, pr, traceParent, traceState);
    }

    /// <summary>Wraps a verified Assessment document into a CloudEvents envelope.</summary>
    public static DiffraCloudEvent FromAssessment(
        AssessmentDocument assessment,
        string repo,
        string? branch = null,
        string? commit = null,
        int? pr = null,
        string? traceParent = null)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        var dataJson = JsonSerializer.Serialize(assessment);
        var dataObject = JsonNode.Parse(dataJson)!.AsObject();
        var source = $"diffra://repo/{repo}/assessment";

        return new DiffraCloudEvent(
            Id: assessment.Id,
            Source: source,
            Type: AssessmentEventType,
            Time: DateTimeOffset.UtcNow,
            DataSchema: AssessmentEvaluator.AssessmentSchema,
            Data: dataObject,
            Subject: commit != null ? $"commit:{commit}" : null,
            DiffraOutcome: assessment.Outcome,
            DiffraDeltaStatus: assessment.DeltaStatus,
            DiffraGitHash: commit,
            DiffraGitBranch: branch,
            DiffraGitHubPr: pr,
            TraceParent: traceParent);
    }
}
