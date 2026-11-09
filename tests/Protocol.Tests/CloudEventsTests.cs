using System.Text.Json;
using System.Text.Json.Nodes;
using Diffra.Protocol;

namespace Diffra.Protocol.Tests;

public static class CloudEventsTests
{
    public static void RunAll()
    {
        SerializesAndDeserializesCloudEvent();
        WrapsAssessmentDocumentIntoCloudEvent();
        Console.WriteLine("PASS CNCF CloudEvents envelope serialization, schema mapping, and assessment projection");
    }

    private static void SerializesAndDeserializesCloudEvent()
    {
        var now = DateTimeOffset.UtcNow;
        var data = new JsonObject
        {
            ["test"] = "payload",
            ["value"] = 42,
        };

        var cloudEvent = new DiffraCloudEvent(
            Id: "sha256:1111111111111111111111111111111111111111111111111111111111111111",
            Source: "diffra://repo/rtkelly-labs/Diffra/subject/diffra/core",
            Type: DiffraCloudEvent.EvidenceEventType,
            Time: now,
            DataSchema: "https://diffra.dev/schemas/evidence-v1.schema.json",
            Data: data,
            Subject: "commit:2222222222222222222222222222222222222222",
            DiffraOutcome: "pass",
            DiffraGitHash: "2222222222222222222222222222222222222222",
            DiffraGitBranch: "main",
            DiffraGitHubPr: 12,
            TraceParent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");

        var json = cloudEvent.ToJsonObject().ToJsonString();
        using var doc = JsonDocument.Parse(json);
        var roundtripped = DiffraCloudEvent.FromJsonElement(doc.RootElement);

        Equal("1.0", DiffraCloudEvent.SpecVersion);
        Equal(cloudEvent.Id, roundtripped.Id);
        Equal(cloudEvent.Source, roundtripped.Source);
        Equal(DiffraCloudEvent.EvidenceEventType, roundtripped.Type);
        Equal("pass", roundtripped.DiffraOutcome);
        Equal("main", roundtripped.DiffraGitBranch);
        Equal(12, roundtripped.DiffraGitHubPr);
        Equal("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", roundtripped.TraceParent);
        Equal(42, roundtripped.Data["value"]!.GetValue<int>());
    }

    private static void WrapsAssessmentDocumentIntoCloudEvent()
    {
        var assessment = new AssessmentDocument(
            Schema: AssessmentEvaluator.AssessmentSchema,
            Id: "sha256:3333333333333333333333333333333333333333333333333333333333333333",
            DeltaId: "sha256:4444444444444444444444444444444444444444444444444444444444444444",
            DeltaStatus: "changed",
            Policy: new PolicyProvenance("diffra.dogfood.quality-budget", "1.0.0", "sha256:5555555555555555555555555555555555555555555555555555555555555555"),
            Outcome: "pass",
            Findings: [],
            Provenance: new AssessmentProvenance("diffra.numeric-policy", "1.0.0", "sha256:4444444444444444444444444444444444444444444444444444444444444444"));

        var ce = DiffraCloudEvent.FromAssessment(
            assessment,
            repo: "rtkelly-labs/Diffra",
            branch: "feature/cas-bundles",
            commit: "6666666666666666666666666666666666666666",
            pr: 12);

        Equal("1.0", DiffraCloudEvent.SpecVersion);
        Equal(DiffraCloudEvent.AssessmentEventType, ce.Type);
        Equal("pass", ce.DiffraOutcome);
        Equal("changed", ce.DiffraDeltaStatus);
        Equal("commit:6666666666666666666666666666666666666666", ce.Subject);
        Equal(12, ce.DiffraGitHubPr);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected: {expected}, Actual: {actual}");
        }
    }
}
