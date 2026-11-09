using System.Text;
using System.Text.Json;
using Diffra.Protocol;

namespace Diffra.Protocol.Tests;

/// <summary>Focused checks for report determinism, safe HTML output, and input pairing.</summary>
public static class ReportingTests
{
    private const string DeltaJson = """
        {
          "schema":"https://diffra.dev/schemas/delta-v1.schema.json",
          "id":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "status":"changed",
          "typeResolution":"recognized",
          "baselineId":"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
          "candidateEvidenceId":"sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
          "comparator":{"id":"diffra.numeric","version":"1.0.0","configDigest":"sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","inputTypeId":"org.example.metrics","inputTypeVersion":"1.0"},
          "changes":[{"path":"/metrics/<script>alert(1)</script>","kind":"modified","value":{"before":"1.25","after":"2.5","absolute":"1.25","relative":"1"},"unit":"unit|<unsafe>&"}],
          "provenance":{"baselineEvidenceId":"sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","candidateEvidenceId":"sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","environment":{"runtime":".NET 10.0"}},
          "message":"<img src=x onerror=alert(1)>&"
        }
        """;

    private const string AssessmentJson = """
        {
          "schema":"https://diffra.dev/schemas/assessment-v1.schema.json",
          "id":"sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
          "deltaId":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "deltaStatus":"changed",
          "policy":{"id":"org.example.policy","version":"2.1.0","rulesDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111"},
          "outcome":"warn",
          "findings":[{"id":"growth-limit","status":"warn","message":"Review <b>growth</b> & impact","changePaths":["/metrics/<script>alert(1)</script>"],"observed":"1.25","expected":"1"}],
          "provenance":{"evaluator":"diffra.policy","version":"1.0.0","inputDeltaId":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}
        }
        """;

    private const string BaselineReferenceJson = """
        {"schema":"https://diffra.dev/schemas/baseline-reference-v1.schema.json","id":"sha256:9999999999999999999999999999999999999999999999999999999999999999","role":"merge-base","status":"trusted","evidenceId":"sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","subjectCommit":"fedcba9876543210","manifestDigest":"sha256:2222222222222222222222222222222222222222222222222222222222222222","resolver":{"id":"diffra.git-merge-base","version":"1.0.0","resolvedCommit":"fedcba9876543210"},"sourceStore":"local","trust":{"issuer":"trusted-ci","decisionId":"run-42"}}
        """;

    public static void RunAll()
    {
        HtmlIsDeterministicAndEscapesEvidenceText();
        MissingAssessmentHasNoVerdict();
        JsonAndMarkdownIncludeReportData();
        MismatchedAssessmentIsRejected();
        InvalidDeltaIsRejected();
        MarkdownEscapesUntrustedText();
        AssessmentProvenanceAndOutcomeAreValidated();
        BaselineReferenceIsVerifiedAndRendered();
        Console.WriteLine("PASS deterministic and escaped reports");
    }

    private static void HtmlIsDeterministicAndEscapesEvidenceText()
    {
        var delta = Bytes(ValidDeltaJson());
        var assessment = Bytes(ValidAssessmentJson());
        var first = ReportRenderer.Render(delta, assessment, ReportFormat.Html);
        var second = ReportRenderer.Render(delta, assessment, ReportFormat.Html);
        Equal(first, second, "Repeated HTML rendering changed bytes.");
        Contains(first, "<!doctype html>");
        Contains(first, "<style>");
        Contains(first, "Baseline evidence ID");
        Contains(first, "Candidate evidence ID");
        Contains(first, "Comparator version");
        Contains(first, "Policy version");
        Contains(first, "warn");
        Contains(first, "&lt;script&gt;alert(1)&lt;/script&gt;");
        Contains(first, "&lt;img src=x onerror=alert(1)&gt;&amp;");
        Contains(first, "&lt;b&gt;growth&lt;/b&gt; &amp; impact");
        DoesNotContain(first, "<script>");
        DoesNotContain(first, "<img src=x");
        DoesNotContain(first, "<b>growth</b>");
        DoesNotContain(first, "<script src=");
    }

    private static void MissingAssessmentHasNoVerdict()
    {
        var html = ReportRenderer.Render(Bytes(ValidDeltaJson()), null, ReportFormat.Html);
        Contains(html, "No policy verdict is available.");
        Contains(html, "No Assessment document was supplied.");
        var json = ReportRenderer.Render(Bytes(ValidDeltaJson()), null, ReportFormat.Json);
        using var document = JsonDocument.Parse(json);
        Equal(false, document.RootElement.GetProperty("hasPolicyVerdict").GetBoolean(), "JSON report verdict flag is wrong.");
        Equal(JsonValueKind.Null, document.RootElement.GetProperty("assessment").ValueKind, "JSON report should preserve the absent assessment as null.");
    }

    private static void JsonAndMarkdownIncludeReportData()
    {
        var delta = Bytes(ValidDeltaJson());
        var assessment = Bytes(ValidAssessmentJson());
        var jsonOne = ReportRenderer.Render(delta, assessment, ReportFormat.Json);
        var jsonTwo = ReportRenderer.Render(delta, assessment, ReportFormat.Json);
        Equal(jsonOne, jsonTwo, "Repeated JSON rendering changed bytes.");
        using var document = JsonDocument.Parse(jsonOne);
        Equal(true, document.RootElement.GetProperty("hasPolicyVerdict").GetBoolean(), "JSON report verdict flag is wrong.");
        Contains(jsonOne, "growth-limit");

        var markdown = ReportRenderer.Render(delta, assessment, ReportFormat.Markdown);
        Contains(markdown, "## Changes");
        Contains(markdown, "## Policy assessment");
        Contains(markdown, "version `2.1.0`");
        Contains(markdown, "unit\\|&lt;unsafe&gt;&amp;");
    }

    private static void MismatchedAssessmentIsRejected()
    {
        var assessment = ValidAssessmentJson().Replace(
            DocumentIdentity.ComputeId("delta", Bytes(DeltaJson)),
            "sha256:9999999999999999999999999999999999999999999999999999999999999999",
            StringComparison.Ordinal);
        assessment = SetId(assessment, "assessment");
        Throws<JsonException>(() => ReportRenderer.Render(Bytes(ValidDeltaJson()), Bytes(assessment), ReportFormat.Html));
    }

    private static void InvalidDeltaIsRejected()
    {
        Throws<JsonException>(() => ReportRenderer.Render(Bytes("{}"), null, ReportFormat.Html));
        var altered = ValidDeltaJson().Replace("changed", "unchanged", StringComparison.Ordinal);
        Throws<JsonException>(() => ReportRenderer.Render(Bytes(altered), null, ReportFormat.Html));

        var valid = ValidDeltaJson();
        using var parsed = JsonDocument.Parse(valid);
        var currentId = parsed.RootElement.GetProperty("id").GetString()!;
        var withUnknown = valid[..^1] + ",\"verdict\":\"pass\"}";
        withUnknown = ReplaceId(withUnknown, "delta", currentId);
        Throws<JsonException>(() => ReportRenderer.Render(Bytes(withUnknown), null, ReportFormat.Html));
    }

    private static void MarkdownEscapesUntrustedText()
    {
        var markdown = ReportRenderer.Render(Bytes(ValidDeltaJson()), Bytes(ValidAssessmentJson()), ReportFormat.Markdown);
        DoesNotContain(markdown, "<script>");
        DoesNotContain(markdown, "<b>");
        Contains(markdown, "&lt;script&gt;");
        Contains(markdown, "&lt;b&gt;growth&lt;/b&gt;");
        Contains(markdown, "&amp;");
    }

    private static void AssessmentProvenanceAndOutcomeAreValidated()
    {
        var wrongProvenance = ValidAssessmentJson().Replace(
            "\"inputDeltaId\":\"" + DocumentIdentity.ComputeId("delta", Bytes(DeltaJson)) + "\"",
            "\"inputDeltaId\":\"sha256:9999999999999999999999999999999999999999999999999999999999999999\"",
            StringComparison.Ordinal);
        wrongProvenance = SetId(wrongProvenance, "assessment");
        Throws<JsonException>(() => ReportRenderer.Render(Bytes(ValidDeltaJson()), Bytes(wrongProvenance), ReportFormat.Html));

        var nonComparable = ValidDeltaJson().Replace("\"status\":\"changed\"", "\"status\":\"unsupported\"", StringComparison.Ordinal);
        nonComparable = SetId(nonComparable, "delta");
        var passingAssessment = ValidAssessmentJson()
            .Replace(DocumentIdentity.ComputeId("delta", Bytes(DeltaJson)), DocumentIdentity.ComputeId("delta", Bytes(nonComparable)), StringComparison.Ordinal)
            .Replace("\"deltaStatus\":\"changed\"", "\"deltaStatus\":\"unsupported\"", StringComparison.Ordinal)
            .Replace("\"outcome\":\"warn\"", "\"outcome\":\"pass\"", StringComparison.Ordinal);
        passingAssessment = SetId(passingAssessment, "assessment");
        Throws<JsonException>(() => ReportRenderer.Render(Bytes(nonComparable), Bytes(passingAssessment), ReportFormat.Html));
    }

    private static void BaselineReferenceIsVerifiedAndRendered()
    {
        var baseline = ReplaceId(BaselineReferenceJson, "baseline-reference", "sha256:9999999999999999999999999999999999999999999999999999999999999999");
        var deltaWithBaseline = ValidDeltaJson().Replace(
            "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            JsonDocument.Parse(baseline).RootElement.GetProperty("id").GetString(),
            StringComparison.Ordinal);
        deltaWithBaseline = SetId(deltaWithBaseline, "delta");
        var html = ReportRenderer.Render(Bytes(deltaWithBaseline), null, Bytes(baseline), ReportFormat.Html);
        Contains(html, "Baseline role");
        Contains(html, "merge-base");
        Contains(html, "Baseline resolved commit");
        Throws<JsonException>(() => ReportRenderer.Render(Bytes(ValidDeltaJson()), null, Bytes(baseline), ReportFormat.Html));
    }

    private static string ValidDeltaJson() => ReplaceId(
        DeltaJson,
        "delta",
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

    private static string ValidAssessmentJson()
    {
        var deltaId = DocumentIdentity.ComputeId("delta", Bytes(DeltaJson));
        var withDeltaId = AssessmentJson.Replace(
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            deltaId,
            StringComparison.Ordinal);
        return ReplaceId(
            withDeltaId,
            "assessment",
            "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
    }

    private static string ReplaceId(string json, string kind, string placeholder)
    {
        var id = DocumentIdentity.ComputeId(kind, Bytes(json));
        return json.Replace(placeholder, id, StringComparison.Ordinal);
    }

    private static string SetId(string json, string kind)
    {
        using var document = JsonDocument.Parse(json);
        var currentId = document.RootElement.GetProperty("id").GetString()!;
        var newId = DocumentIdentity.ComputeId(kind, Bytes(json));
        return json.Replace("\"id\":\"" + currentId + "\"", "\"id\":\"" + newId + "\"", StringComparison.Ordinal);
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static void Contains(string value, string expected)
    {
        if (!value.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected report to contain: {expected}");
        }
    }

    private static void DoesNotContain(string value, string unexpected)
    {
        if (value.Contains(unexpected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Report unexpectedly contains: {unexpected}");
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected <{expected}> but got <{actual}>.");
        }
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
    }
}
