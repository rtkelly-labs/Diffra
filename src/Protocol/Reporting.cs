using System.Net;
using System.Text;
using System.Text.Json;

namespace Diffra.Protocol;

/// <summary>Output formats supported by the deterministic report renderer.</summary>
public enum ReportFormat
{
    Html,
    Json,
    Markdown,
}

/// <summary>Renders review reports from immutable Delta and optional Assessment documents.</summary>
public static class ReportRenderer
{
    public const string DeltaSchema = "https://diffra.dev/schemas/delta-v1.schema.json";
    public const string AssessmentSchema = "https://diffra.dev/schemas/assessment-v1.schema.json";
    public const string BaselineReferenceSchema = "https://diffra.dev/schemas/baseline-reference-v1.schema.json";

    private const string HtmlCss = ":root,[data-theme=\"sketch\"]{--ds-surface-base:#f7f9fb;--ds-surface-raised:#ffffff;--ds-surface-sunken:#eef3f7;--ds-text-primary:#18212b;--ds-text-secondary:#4b5563;--ds-text-muted:#6b7280;--ds-border-strong:#18212b;--ds-border-default:#d5dde5;--ds-intent-success:#107c41;--ds-intent-warning:#b45309;--ds-intent-danger:#b91c1c;--ds-intent-info:#0369a1}@media(prefers-color-scheme:dark){:root:not([data-theme=\"sketch\"]){--ds-surface-base:#0f172a;--ds-surface-raised:#1e293b;--ds-surface-sunken:#090d16;--ds-text-primary:#f8fafc;--ds-text-secondary:#cbd5e1;--ds-text-muted:#94a3b8;--ds-border-strong:#f8fafc;--ds-border-default:#334155;--ds-intent-success:#22c55e;--ds-intent-warning:#f59e0b;--ds-intent-danger:#ef4444;--ds-intent-info:#38bdf8}}[data-theme=\"midnight\"]{--ds-surface-base:#0f172a;--ds-surface-raised:#1e293b;--ds-surface-sunken:#090d16;--ds-text-primary:#f8fafc;--ds-text-secondary:#cbd5e1;--ds-text-muted:#94a3b8;--ds-border-strong:#f8fafc;--ds-border-default:#334155;--ds-intent-success:#22c55e;--ds-intent-warning:#f59e0b;--ds-intent-danger:#ef4444;--ds-intent-info:#38bdf8}body{font-family:system-ui,-apple-system,BlinkMacSystemFont,\"Segoe UI\",Roboto,sans-serif;line-height:1.5;margin:0 auto;max-width:72rem;padding:1.5rem;color:var(--ds-text-primary);background:var(--ds-surface-base)}main{background:var(--ds-surface-raised);border:1px solid var(--ds-border-default);border-radius:.5rem;padding:clamp(1rem,3vw,2rem);box-shadow:0 1px 3px rgba(0,0,0,0.05)}h1,h2,h3{line-height:1.2}h1{margin-top:0;font-size:1.75rem}.verdict-banner{display:flex;align-items:center;gap:1rem;border-left:6px solid var(--ds-border-strong);background:var(--ds-surface-sunken);padding:1rem 1.25rem;margin:1.25rem 0;border-radius:.25rem}.verdict-pass{border-color:var(--ds-intent-success)}.verdict-warn{border-color:var(--ds-intent-warning)}.verdict-fail{border-color:var(--ds-intent-danger)}.verdict-title{font-size:1.25rem;font-weight:800;letter-spacing:.05em;display:block}.verdict-sub{margin:.25rem 0 0;color:var(--ds-text-secondary);font-size:.9rem}.stat-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(10rem,1fr));gap:.75rem;margin:1.25rem 0}.stat-card{background:var(--ds-surface-sunken);border:1px solid var(--ds-border-default);border-radius:.375rem;padding:.75rem 1rem;display:flex;flex-direction:column}.stat-label{font-size:.75rem;font-weight:700;color:var(--ds-text-muted);letter-spacing:.05em;text-transform:uppercase}.stat-value{font-size:1.5rem;font-weight:800;margin-top:.25rem}dl{display:grid;grid-template-columns:minmax(10rem,15rem) 1fr;gap:.35rem 1rem}dt{font-weight:700}dd{margin:0;overflow-wrap:anywhere}table{border-collapse:collapse;width:100%;margin:1rem 0}caption{text-align:left;font-weight:700;padding:.5rem 0}th,td{border:1px solid var(--ds-border-default);padding:.55rem;text-align:left;vertical-align:top}th{background:var(--ds-surface-sunken)}code,pre{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;overflow-wrap:anywhere}pre{white-space:pre-wrap;margin:0}.status{font-weight:700;text-transform:uppercase}.notice{border-left:.3rem solid var(--ds-intent-info);background:var(--ds-surface-sunken);padding:.75rem 1rem}li{margin:.5rem 0}@media(max-width:40rem){dl{grid-template-columns:1fr;gap:.1rem}dd{margin:0 0 .6rem}}";

    /// <summary>Renders a validated Delta and optional matching Assessment.</summary>
    public static string Render(
        ReadOnlyMemory<byte> deltaUtf8Json,
        ReadOnlyMemory<byte>? assessmentUtf8Json,
        ReportFormat format)
        => Render(deltaUtf8Json, assessmentUtf8Json, null, format);

    /// <summary>Renders a Delta, optional matching Assessment, and optional verified Baseline reference.</summary>
    public static string Render(
        ReadOnlyMemory<byte> deltaUtf8Json,
        ReadOnlyMemory<byte>? assessmentUtf8Json,
        ReadOnlyMemory<byte>? baselineReferenceUtf8Json,
        ReportFormat format)
    {
        DocumentIdentity.Verify("delta", deltaUtf8Json.Span);
        using var deltaDocument = CanonicalJson.Parse(deltaUtf8Json.Span);
        ValidateDelta(deltaDocument.RootElement);

        JsonDocument? assessmentDocument = null;
        JsonDocument? baselineDocument = null;
        try
        {
            if (baselineReferenceUtf8Json is { } baselineBytes)
            {
                DocumentIdentity.Verify("baseline-reference", baselineBytes.Span);
                baselineDocument = CanonicalJson.Parse(baselineBytes.Span);
                ValidateBaselineReference(baselineDocument.RootElement, deltaDocument.RootElement);
            }
            if (assessmentUtf8Json is { } assessmentBytes)
            {
                DocumentIdentity.Verify("assessment", assessmentBytes.Span);
                assessmentDocument = CanonicalJson.Parse(assessmentBytes.Span);
                ValidateAssessment(assessmentDocument.RootElement, deltaDocument.RootElement);
            }

            return format switch
            {
                ReportFormat.Html => RenderHtml(deltaDocument.RootElement, assessmentDocument?.RootElement, baselineDocument?.RootElement),
                ReportFormat.Json => RenderJson(deltaDocument.RootElement, assessmentDocument?.RootElement, baselineDocument?.RootElement),
                ReportFormat.Markdown => RenderMarkdown(deltaDocument.RootElement, assessmentDocument?.RootElement, baselineDocument?.RootElement),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported report format."),
            };
        }
        finally
        {
            assessmentDocument?.Dispose();
            baselineDocument?.Dispose();
        }
    }

    private static string RenderHtml(JsonElement delta, JsonElement? assessment, JsonElement? baseline)
    {
        var builder = new StringBuilder(4096);
        builder.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>Diffra comparison report</title><style>")
            .Append(HtmlCss)
            .Append("</style></head><body><main><h1>Diffra comparison report</h1>");

        AppendVerdictBannerHtml(builder, assessment, delta);
        AppendStatGridHtml(builder, delta, assessment, baseline);
        AppendDeltaSummaryHtml(builder, delta, baseline);
        AppendChangesHtml(builder, delta.GetProperty("changes"));
        if (assessment is { } assessmentValue)
        {
            AppendAssessmentHtml(builder, assessmentValue);
        }
        else
        {
            builder.Append("<section aria-labelledby=\"policy-heading\"><h2 id=\"policy-heading\">Policy assessment</h2><p class=\"notice\" role=\"status\">No policy verdict is available. No Assessment document was supplied.</p></section>");
        }

        return builder.Append("</main></body></html>").ToString();
    }

    private static void AppendVerdictBannerHtml(StringBuilder builder, JsonElement? assessment, JsonElement delta)
    {
        if (assessment is { } a)
        {
            var outcome = a.GetProperty("outcome").GetString()!;
            var (vClass, vTitle, vSub) = outcome switch
            {
                "pass" => ("verdict-pass", "PASS", "All evaluated policy rules passed successfully."),
                "warn" => ("verdict-warn", "WARN", "Policy rules evaluated with warnings requiring review."),
                "fail" => ("verdict-fail", "FAIL", "One or more policy rules failed."),
                _ => ("verdict-notice", outcome.ToUpperInvariant(), "Assessment completed with status: " + outcome)
            };
            builder.Append("<div class=\"verdict-banner ").Append(vClass).Append("\"><div><span class=\"verdict-title\">")
                .Append(vTitle).Append("</span><p class=\"verdict-sub\">").Append(vSub).Append("</p></div></div>");
        }
        else
        {
            builder.Append("<div class=\"verdict-banner\"><div><span class=\"verdict-title\">NO POLICY VERDICT</span><p class=\"verdict-sub\">Report describes measured changes without policy assessment.</p></div></div>");
        }
    }

    private static void AppendStatGridHtml(StringBuilder builder, JsonElement delta, JsonElement? assessment, JsonElement? baseline)
    {
        var status = delta.GetProperty("status").GetString()!;
        var changesCount = delta.GetProperty("changes").GetArrayLength();
        var findingsCount = assessment?.GetProperty("findings").GetArrayLength() ?? 0;
        var baselineRole = baseline?.GetProperty("role").GetString() ?? "none";

        builder.Append("<div class=\"stat-grid\">");
        builder.Append("<div class=\"stat-card\"><span class=\"stat-label\">Status</span><span class=\"stat-value\">").Append(Escape(status)).Append("</span></div>");
        builder.Append("<div class=\"stat-card\"><span class=\"stat-label\">Changes</span><span class=\"stat-value\">").Append(changesCount).Append("</span></div>");
        builder.Append("<div class=\"stat-card\"><span class=\"stat-label\">Findings</span><span class=\"stat-value\">").Append(findingsCount).Append("</span></div>");
        builder.Append("<div class=\"stat-card\"><span class=\"stat-label\">Baseline</span><span class=\"stat-value\">").Append(Escape(baselineRole)).Append("</span></div>");
        builder.Append("</div>");
    }

    private static void AppendDeltaSummaryHtml(StringBuilder builder, JsonElement delta, JsonElement? baseline)
    {
        var comparator = delta.GetProperty("comparator");
        var provenance = delta.GetProperty("provenance");
        builder.Append("<section aria-labelledby=\"comparison-heading\"><h2 id=\"comparison-heading\">Comparison</h2><dl>");
        HtmlField(builder, "Delta ID", delta.GetProperty("id").GetString()!);
        HtmlField(builder, "Status", delta.GetProperty("status").GetString()!, "status");
        HtmlField(builder, "Baseline reference ID", delta.GetProperty("baselineId").GetString()!);
        if (baseline is { } reference)
        {
            HtmlField(builder, "Baseline role", reference.GetProperty("role").GetString()!);
            HtmlField(builder, "Baseline resolved commit", reference.GetProperty("subjectCommit").GetString()!);
        }
        HtmlField(builder, "Baseline evidence ID", provenance.GetProperty("baselineEvidenceId").GetString()!);
        HtmlField(builder, "Candidate evidence ID", delta.GetProperty("candidateEvidenceId").GetString()!);
        HtmlField(builder, "Comparator", comparator.GetProperty("id").GetString()!);
        HtmlField(builder, "Comparator version", comparator.GetProperty("version").GetString()!);
        HtmlField(builder, "Evidence type", $"{comparator.GetProperty("inputTypeId").GetString()} v{comparator.GetProperty("inputTypeVersion").GetString()}");
        builder.Append("</dl>");
        if (delta.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
        {
            builder.Append("<p class=\"notice\">").Append(Escape(message.GetString()!)).Append("</p>");
        }

        builder.Append("</section>");
    }

    private static void AppendChangesHtml(StringBuilder builder, JsonElement changes)
    {
        builder.Append("<section aria-labelledby=\"changes-heading\"><h2 id=\"changes-heading\">Changes</h2><table><caption>Recorded Delta changes</caption><thead><tr><th scope=\"col\">Path</th><th scope=\"col\">Kind</th><th scope=\"col\">Value</th><th scope=\"col\">Unit</th></tr></thead><tbody>");
        foreach (var change in Ordered(changes, "path"))
        {
            builder.Append("<tr><th scope=\"row\"><code>").Append(Escape(change.GetProperty("path").GetString()!)).Append("</code></th><td>")
                .Append(Escape(change.GetProperty("kind").GetString()!)).Append("</td><td><pre>")
                .Append(Escape(CanonicalValue(change.GetProperty("value")))).Append("</pre></td><td>")
                .Append(change.TryGetProperty("unit", out var unit) && unit.ValueKind == JsonValueKind.String ? Escape(unit.GetString()!) : "<span aria-label=\"no unit\">—</span>")
                .Append("</td></tr>");
        }

        if (changes.GetArrayLength() == 0)
        {
            builder.Append("<tr><td colspan=\"4\">No changes were recorded.</td></tr>");
        }

        builder.Append("</tbody></table></section>");
    }

    private static void AppendAssessmentHtml(StringBuilder builder, JsonElement assessment)
    {
        var policy = assessment.GetProperty("policy");
        builder.Append("<section aria-labelledby=\"policy-heading\"><h2 id=\"policy-heading\">Policy assessment</h2><dl>");
        HtmlField(builder, "Assessment ID", assessment.GetProperty("id").GetString()!);
        HtmlField(builder, "Policy", policy.GetProperty("id").GetString()!);
        HtmlField(builder, "Policy version", policy.GetProperty("version").GetString()!);
        HtmlField(builder, "Outcome", assessment.GetProperty("outcome").GetString()!, "status");
        builder.Append("</dl><h3>Findings</h3><ul>");
        foreach (var finding in Ordered(assessment.GetProperty("findings"), "id"))
        {
            builder.Append("<li><strong>").Append(Escape(finding.GetProperty("status").GetString()!)).Append("</strong> ")
                .Append(Escape(finding.GetProperty("message").GetString()!)).Append(" <code>")
                .Append(Escape(finding.GetProperty("id").GetString()!)).Append("</code>");
            if (finding.TryGetProperty("changePaths", out var paths) && paths.GetArrayLength() > 0)
            {
                builder.Append("<ul>");
                foreach (var path in paths.EnumerateArray().Select(value => value.GetString()!).Order(StringComparer.Ordinal))
                {
                    builder.Append("<li><code>").Append(Escape(path)).Append("</code></li>");
                }

                builder.Append("</ul>");
            }

            builder.Append("</li>");
        }

        if (assessment.GetProperty("findings").GetArrayLength() == 0)
        {
            builder.Append("<li>No findings were recorded.</li>");
        }

        builder.Append("</ul></section>");
    }

    private static void HtmlField(StringBuilder builder, string label, string value, string? cssClass = null)
    {
        builder.Append("<dt>").Append(Escape(label)).Append("</dt><dd");
        if (cssClass is not null)
        {
            builder.Append(" class=\"").Append(Escape(cssClass)).Append('\"');
        }

        builder.Append('>').Append(Escape(value)).Append("</dd>");
    }

    private static string RenderJson(JsonElement delta, JsonElement? assessment, JsonElement? baseline)
    {
        var deltaCanonical = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(delta.GetRawText()));
        var assessmentCanonical = assessment is { } value
            ? CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(value.GetRawText()))
            : null;
        var assessmentJson = assessmentCanonical is null ? "null" : Encoding.UTF8.GetString(assessmentCanonical);
        var baselineCanonical = baseline is { } baselineValue ? CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(baselineValue.GetRawText())) : null;
        var baselineJson = baselineCanonical is null ? "null" : Encoding.UTF8.GetString(baselineCanonical);
        var output = $"{{\"assessment\":{assessmentJson},\"baselineReference\":{baselineJson},\"delta\":{Encoding.UTF8.GetString(deltaCanonical)},\"hasPolicyVerdict\":{(assessment is null ? "false" : "true")},\"schema\":\"https://diffra.dev/schemas/report-v1.json\"}}";
        return Encoding.UTF8.GetString(CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(output)));
    }

    private static string RenderMarkdown(JsonElement delta, JsonElement? assessment, JsonElement? baseline)
    {
        var comparator = delta.GetProperty("comparator");
        var provenance = delta.GetProperty("provenance");
        var builder = new StringBuilder();
        builder.AppendLine("# Diffra comparison report").AppendLine()
            .Append("- Delta ID: `").Append(MarkdownCell(delta.GetProperty("id").GetString()!)).AppendLine("`")
            .Append("- Status: `").Append(MarkdownCell(delta.GetProperty("status").GetString()!)).AppendLine("`")
            .Append("- Baseline reference ID: `").Append(MarkdownCell(delta.GetProperty("baselineId").GetString()!)).AppendLine("`")
            .Append("- Baseline evidence ID: `").Append(MarkdownCell(provenance.GetProperty("baselineEvidenceId").GetString()!)).AppendLine("`")
            .Append("- Candidate evidence ID: `").Append(MarkdownCell(delta.GetProperty("candidateEvidenceId").GetString()!)).AppendLine("`")
            .Append("- Comparator: `").Append(MarkdownCell(comparator.GetProperty("id").GetString()!)).Append("` version `").Append(MarkdownCell(comparator.GetProperty("version").GetString()!)).AppendLine("`");

        if (baseline is { } baselineValue)
        {
            builder.Append("- Baseline role: `").Append(MarkdownCell(baselineValue.GetProperty("role").GetString()!)).AppendLine("`")
                .Append("- Baseline resolved commit: `").Append(MarkdownCell(baselineValue.GetProperty("subjectCommit").GetString()!)).AppendLine("`");
        }

        builder.AppendLine().AppendLine("## Changes").AppendLine()
            .AppendLine("| Path | Kind | Value | Unit |").AppendLine("| --- | --- | --- | --- |");

        foreach (var change in Ordered(delta.GetProperty("changes"), "path"))
        {
            builder.Append("| `").Append(MarkdownCell(change.GetProperty("path").GetString()!)).Append("` | `")
                .Append(MarkdownCell(change.GetProperty("kind").GetString()!)).Append("` | `")
                .Append(MarkdownCell(CanonicalValue(change.GetProperty("value")))).Append("` | ")
                .Append(change.TryGetProperty("unit", out var unit) && unit.ValueKind == JsonValueKind.String ? MarkdownCell(unit.GetString()!) : "").AppendLine(" |");
        }

        if (assessment is not { } assessmentValue)
        {
            builder.AppendLine().AppendLine("## Policy assessment").AppendLine()
                .AppendLine("No policy verdict is available. No Assessment document was supplied.");
        }
        else
        {
            var policy = assessmentValue.GetProperty("policy");
            builder.AppendLine().AppendLine("## Policy assessment").AppendLine()
                .Append("- Policy: `").Append(policy.GetProperty("id").GetString()).Append("` version `").Append(policy.GetProperty("version").GetString()).AppendLine("`")
                .Append("- Outcome: `").Append(assessmentValue.GetProperty("outcome").GetString()).AppendLine("`")
                .AppendLine().AppendLine("### Findings").AppendLine();
            foreach (var finding in Ordered(assessmentValue.GetProperty("findings"), "id"))
            {
                builder.Append("- **").Append(MarkdownCell(finding.GetProperty("status").GetString()!)).Append("** ")
                    .Append(MarkdownCell(finding.GetProperty("message").GetString()!)).Append(" (`")
                    .Append(MarkdownCell(finding.GetProperty("id").GetString()!)).AppendLine("`)");
            }

            if (assessmentValue.GetProperty("findings").GetArrayLength() == 0)
            {
                builder.AppendLine("- No findings were recorded.");
            }
        }

        return builder.ToString();
    }

    private static void ValidateDelta(JsonElement delta)
    {
        RequireSchema(delta, DeltaSchema, "Delta");
        RequireOnlyProperties(delta, "schema", "id", "status", "typeResolution", "baselineId", "candidateEvidenceId", "comparator", "changes", "provenance", "message");
        RequireString(delta, "id");
        RequireString(delta, "baselineId");
        RequireString(delta, "candidateEvidenceId");
        if (RequireString(delta, "typeResolution") is not ("recognized" or "opaque")) throw new JsonException("Delta typeResolution is invalid.");
        var status = RequireString(delta, "status");
        if (status is not ("unchanged" or "changed" or "unavailable" or "invalid" or "incompatible" or "inconclusive" or "unsupported"))
        {
            throw new JsonException($"Delta status '{status}' is not supported.");
        }

        var comparator = RequiredObject(delta, "comparator");
        RequireOnlyProperties(comparator, "id", "version", "configDigest", "inputTypeId", "inputTypeVersion");
        RequireString(comparator, "id");
        RequireString(comparator, "version");
        RequireString(comparator, "configDigest");
        RequireString(comparator, "inputTypeId");
        RequireString(comparator, "inputTypeVersion");
        var provenance = RequiredObject(delta, "provenance");
        RequireOnlyProperties(provenance, "baselineEvidenceId", "candidateEvidenceId", "environment");
        RequireString(provenance, "baselineEvidenceId");
        if (!string.Equals(RequireString(provenance, "candidateEvidenceId"), RequireString(delta, "candidateEvidenceId"), StringComparison.Ordinal))
        {
            throw new JsonException("Delta provenance candidateEvidenceId does not match the candidate.");
        }
        if (provenance.GetProperty("environment").ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Delta provenance environment must be an object.");
        }

        if (delta.GetProperty("changes").ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Delta changes must be an array.");
        }
        foreach (var change in delta.GetProperty("changes").EnumerateArray())
        {
            RequireOnlyProperties(change, "path", "kind", "value", "unit", "metadata");
            RequireString(change, "path");
            if (RequireString(change, "kind") is not ("added" or "removed" or "modified" or "missing" or "incompatible" or "unavailable"))
                throw new JsonException("Delta change kind is invalid.");
            if (!change.TryGetProperty("value", out _)) throw new JsonException("Delta change value is missing.");
            if (change.TryGetProperty("unit", out var unit) && unit.ValueKind != JsonValueKind.String)
                throw new JsonException("Delta change unit must be a string.");
            if (change.TryGetProperty("metadata", out var metadata) && metadata.ValueKind != JsonValueKind.Object)
                throw new JsonException("Delta change metadata must be an object.");
        }

        if (status == "changed" && delta.GetProperty("changes").GetArrayLength() == 0)
        {
            throw new JsonException("A changed Delta must contain at least one change.");
        }

        if (status == "unchanged" && delta.GetProperty("changes").GetArrayLength() != 0)
        {
            throw new JsonException("An unchanged Delta cannot contain changes.");
        }
    }

    private static void ValidateAssessment(JsonElement assessment, JsonElement delta)
    {
        RequireSchema(assessment, AssessmentSchema, "Assessment");
        RequireOnlyProperties(assessment, "schema", "id", "deltaId", "deltaStatus", "policy", "outcome", "findings", "provenance");
        RequireString(assessment, "id");
        if (!string.Equals(RequireString(assessment, "deltaId"), RequireString(delta, "id"), StringComparison.Ordinal))
        {
            throw new JsonException("Assessment deltaId does not match the supplied Delta ID.");
        }

        if (!string.Equals(RequireString(assessment, "deltaStatus"), RequireString(delta, "status"), StringComparison.Ordinal))
        {
            throw new JsonException("Assessment deltaStatus does not match the supplied Delta status.");
        }

        var outcome = RequireString(assessment, "outcome");
        if (outcome is not ("pass" or "warn" or "fail" or "needs-review" or "inconclusive"))
        {
            throw new JsonException($"Assessment outcome '{outcome}' is not supported.");
        }

        var provenance = RequiredObject(assessment, "provenance");
        RequireOnlyProperties(provenance, "evaluator", "version", "inputDeltaId");
        RequireString(provenance, "evaluator");
        RequireString(provenance, "version");
        if (!string.Equals(RequireString(provenance, "inputDeltaId"), RequireString(delta, "id"), StringComparison.Ordinal))
        {
            throw new JsonException("Assessment provenance inputDeltaId does not match the supplied Delta ID.");
        }

        var nonComparable = RequireString(delta, "status") is "unavailable" or "invalid" or "incompatible" or "inconclusive" or "unsupported";
        if (nonComparable && (outcome is "pass" or "warn"))
        {
            throw new JsonException("A non-comparable Delta cannot receive a passing or warning Assessment.");
        }

        var policy = RequiredObject(assessment, "policy");
        RequireOnlyProperties(policy, "id", "version", "rulesDigest");
        RequireString(policy, "id");
        RequireString(policy, "version");
        RequireString(policy, "rulesDigest");
        var findings = assessment.GetProperty("findings");
        if (findings.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Assessment findings must be an array.");
        }

        foreach (var finding in findings.EnumerateArray())
        {
            RequireOnlyProperties(finding, "id", "status", "message", "changePaths", "observed", "expected");
            RequireString(finding, "id");
            if (RequireString(finding, "status") is not ("pass" or "warn" or "fail" or "needs-review" or "inconclusive"))
                throw new JsonException("Assessment finding status is invalid.");
            RequireString(finding, "message");
            if (finding.GetProperty("changePaths").ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Finding changePaths must be an array.");
            }
        }
    }

    private static void ValidateBaselineReference(JsonElement baseline, JsonElement delta)
    {
        RequireSchema(baseline, BaselineReferenceSchema, "Baseline reference");
        RequireOnlyProperties(baseline, "schema", "id", "role", "status", "evidenceId", "subjectCommit", "manifestDigest", "resolver", "sourceStore", "trust");
        if (!string.Equals(RequireString(baseline, "id"), RequireString(delta, "baselineId"), StringComparison.Ordinal))
            throw new JsonException("Baseline reference ID does not match the Delta baselineId.");
        var role = RequireString(baseline, "role");
        if (role is not ("merge-base" or "approved-main" or "previous-release")) throw new JsonException("Baseline reference role is unsupported.");
        if (RequireString(baseline, "status") != "trusted") throw new JsonException("Report baseline reference must have trusted status.");
        RequireString(baseline, "evidenceId");
        RequireString(baseline, "manifestDigest");
        RequireString(baseline, "sourceStore");
        var commit = RequireString(baseline, "subjectCommit");
        var resolver = RequiredObject(baseline, "resolver");
        RequireOnlyProperties(resolver, "id", "version", "resolvedCommit", "sourceRef");
        RequireString(resolver, "id");
        RequireString(resolver, "version");
        if (!string.Equals(commit, RequireString(resolver, "resolvedCommit"), StringComparison.Ordinal))
            throw new JsonException("Baseline subjectCommit does not match resolver resolvedCommit.");
        var trust = RequiredObject(baseline, "trust");
        RequireOnlyProperties(trust, "issuer", "decisionId", "decisionDigest");
        RequireString(trust, "issuer");
        RequireString(trust, "decisionId");
    }

    private static void RequireSchema(JsonElement document, string expectedSchema, string name)
    {
        if (document.ValueKind != JsonValueKind.Object || !string.Equals(RequireString(document, "schema"), expectedSchema, StringComparison.Ordinal))
        {
            throw new JsonException($"{name} schema is missing or unsupported.");
        }
    }

    private static JsonElement RequiredObject(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"Required object '{property}' is missing or invalid.");
        }

        return value;
    }

    private static string RequireString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new JsonException($"Required string '{property}' is missing or invalid.");
        }

        return value.GetString()!;
    }

    private static void RequireOnlyProperties(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a protocol object.");
        var names = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name)) throw new JsonException($"Unknown protocol property '{property.Name}'.");
        }
    }

    private static IEnumerable<JsonElement> Ordered(JsonElement array, string property) =>
        array.EnumerateArray().OrderBy(item => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : string.Empty, StringComparer.Ordinal);

    private static string CanonicalValue(JsonElement value) =>
        Encoding.UTF8.GetString(CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(value.GetRawText())));

    private static string Escape(string value) => WebUtility.HtmlEncode(value);

    private static string MarkdownCell(string value)
    {
        var builder = new StringBuilder(value.Length + 16);
        foreach (var c in value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal))
        {
            builder.Append(c switch
            {
                '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '`' => "&#96;", '|' => "\\|",
                '\\' or '*' or '_' or '{' or '}' or '[' or ']' or '(' or ')' or '#' or '+' or '-' or '.' or '!' => "\\" + c,
                _ => c.ToString(),
            });
        }
        return builder.ToString();
    }
}
