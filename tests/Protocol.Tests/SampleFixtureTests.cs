using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Diffra.Protocol;

namespace Diffra.Protocol.Tests;

public static class SampleFixtureTests
{
    public static void RunAll()
    {
        var root = FindRepositoryRoot();
        var sample = Path.Combine(root, "examples", "sample-project", "workflow");
        var baseline = File.ReadAllBytes(Path.Combine(sample, "baseline-evidence.json"));
        var candidate = File.ReadAllBytes(Path.Combine(sample, "candidate-evidence.json"));
        var manifest = File.ReadAllBytes(Path.Combine(sample, "baseline-manifest.json"));
        var reference = File.ReadAllBytes(Path.Combine(sample, "baseline-reference.json"));
        var delta = File.ReadAllBytes(Path.Combine(sample, "delta.json"));
        var assessment = File.ReadAllBytes(Path.Combine(sample, "assessment.json"));

        EvidenceIdentity.Verify(baseline);
        EvidenceIdentity.Verify(candidate);
        DocumentIdentity.Verify("manifest", manifest);
        DocumentIdentity.Verify("baseline-reference", reference);
        DocumentIdentity.Verify("delta", delta);
        DocumentIdentity.Verify("assessment", assessment);

        using var baselineDoc = JsonDocument.Parse(baseline);
        using var candidateDoc = JsonDocument.Parse(candidate);
        using var referenceDoc = JsonDocument.Parse(reference);
        using var deltaDoc = JsonDocument.Parse(delta);
        using var assessmentDoc = JsonDocument.Parse(assessment);
        var baselineId = baselineDoc.RootElement.GetProperty("id").GetString();
        var candidateId = candidateDoc.RootElement.GetProperty("id").GetString();
        var referenceId = referenceDoc.RootElement.GetProperty("id").GetString();
        var deltaId = deltaDoc.RootElement.GetProperty("id").GetString();

        Check(referenceDoc.RootElement.GetProperty("evidenceId").GetString() == baselineId, "Baseline reference points at another Evidence document.");
        Check(referenceDoc.RootElement.GetProperty("manifestDigest").GetString() ==
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(manifest)), "Baseline manifest bytes changed.");
        Check(deltaDoc.RootElement.GetProperty("baselineId").GetString() == referenceId, "Delta points at another Baseline reference.");
        Check(deltaDoc.RootElement.GetProperty("candidateEvidenceId").GetString() == candidateId, "Delta points at another candidate.");
        Check(assessmentDoc.RootElement.GetProperty("deltaId").GetString() == deltaId, "Assessment points at another Delta.");

        var report = ReportRenderer.Render(delta, assessment, reference, ReportFormat.Html);
        var checkedInReport = File.ReadAllText(Path.Combine(sample, "report.html"), Encoding.UTF8);
        Check(report == checkedInReport, "Checked-in HTML report differs from deterministic rendering.");
        Console.WriteLine("PASS checked-in workflow identities and report");
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Diffra.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not find Diffra.slnx above the test assembly.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
