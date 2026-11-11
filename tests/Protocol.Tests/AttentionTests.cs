using System.Text;
using System.Text.Json;
using Diffra.Protocol;

namespace Diffra.Protocol.Tests;

/// <summary>Checks the attention levels and the one-screen comment and agent formats.</summary>
public static class AttentionTests
{
    private const string Placeholder = "sha256:0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly string[] ChangePaths = ["/metrics/a"];

    public static void RunAll()
    {
        StatusesMapToLevels();
        FindingsRankByLevelSeverityAndChange();
        QuietResultPostsNothing();
        CommentShowsLevelAndTable();
        CommentOverflowsIntoCollapsedSection();
        CommentKeepsToTheLineLimit();
        CommentKeepsToTheColumnLimit();
        CommentEscapesUntrustedText();
        AgentFormatStaysWithinTokenBudget();
        AgentFormatStatesLevelWithoutAssessment();
        UncomparableDeltaIsWorthReviewing();
        OutputIsDeterministic();
        LimitsAreValidated();
        Console.WriteLine("PASS attention levels and one-screen output");
    }

    private static void StatusesMapToLevels()
    {
        Equal(AttentionLevel.Act, Attention.ForStatus("fail"), "fail");
        Equal(AttentionLevel.Review, Attention.ForStatus("warn"), "warn");
        Equal(AttentionLevel.Review, Attention.ForStatus("needs-review"), "needs-review");
        Equal(AttentionLevel.Review, Attention.ForStatus("inconclusive"), "inconclusive");
        Equal(AttentionLevel.Review, Attention.ForStatus("something-new"), "an unknown status is never quiet");
        Equal(AttentionLevel.Quiet, Attention.ForStatus("pass"), "pass");
        Equal("act", Attention.Token(AttentionLevel.Act), "act token");
        Equal("Needs action", Attention.Label(AttentionLevel.Act), "act label");
        Equal("Worth reviewing", Attention.Label(AttentionLevel.Review), "review label");
        Equal(AttentionLevel.Quiet, Attention.ForFindings([]), "no findings are quiet");
    }

    private static void FindingsRankByLevelSeverityAndChange()
    {
        RankedFinding[] findings =
        [
            Finding("pass", "a"),
            Finding("warn", "small", relative: "0.1"),
            Finding("warn", "large", relative: "-0.9"),
            Finding("needs-review", "zero", relative: null),
            Finding("fail", "failed", relative: "0.01"),
            Finding("inconclusive", "unknown"),
            Finding("warn", "undefined", relative: null),
        ];
        var order = Attention.Rank(findings).Select(f => f.Id).ToArray();
        Equal(
            "failed,unknown,zero,undefined,large,small,a",
            string.Join(',', order),
            "Findings must rank by level, then severity, then the size of the relative change.");
    }

    private static void QuietResultPostsNothing()
    {
        var (delta, assessment) = Documents(["""{"id":"ok","status":"pass","message":"Within limits.","changePaths":[]}"""], "pass");
        Equal(string.Empty, ReportRenderer.Render(delta, assessment, ReportFormat.Comment), "A quiet result must render an empty comment.");
        var agent = ReportRenderer.Render(delta, assessment, ReportFormat.Agent);
        Equal("attention: quiet\nfindings: 0\n", agent, "A quiet agent file states the level and nothing else.");

        var unchanged = UnchangedDelta();
        Equal(string.Empty, ReportRenderer.Render(unchanged, null, ReportFormat.Comment), "An unchanged Delta without rules is quiet.");
    }

    private static void CommentShowsLevelAndTable()
    {
        var (delta, assessment) = Documents(
        [
            Observed("size", "warn", "1000", "1200", "200", "0.2"),
            Observed("time", "fail", "50", "100", "50", "1"),
            """{"id":"ok","status":"pass","message":"Within limits.","changePaths":[]}""",
        ], "fail");
        var comment = ReportRenderer.Render(delta, assessment, ReportFormat.Comment);
        var lines = comment.Split('\n');
        Equal("### Needs action", lines[0], "The comment must open with the level label.");
        Equal("| Finding | Before | After | Change |", lines[2], "The comment table has four columns.");
        Equal("| **fail** `time` | `50` | `100` | `+50 (+100%)` |", lines[4], "The failed rule ranks first.");
        Equal("| **warn** `size` | `1000` | `1200` | `+200 (+20%)` |", lines[5], "The warning ranks second.");
        DoesNotContain(comment, "`ok`");
        DoesNotContain(comment, "<details>");
    }

    private static void CommentOverflowsIntoCollapsedSection()
    {
        var findings = Enumerable.Range(1, 8)
            .Select(i => Observed($"metric-{i}", "warn", "100", (100 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"0.0{i}"))
            .ToArray();
        var (delta, assessment) = Documents(findings, "warn");
        var comment = ReportRenderer.Render(delta, assessment, ReportFormat.Comment);
        var visible = comment[..comment.IndexOf("<details>", StringComparison.Ordinal)];
        Equal(5, CountRows(visible), "Five findings show before the overflow line.");
        Contains(visible, "3 more findings are not shown.");
        Contains(comment, "<details><summary>All 8 findings</summary>");
        Equal(8, CountRows(comment[comment.IndexOf("<details>", StringComparison.Ordinal)..]), "The collapsed section lists every finding.");
        Contains(visible, "`metric-8`");
        DoesNotContain(visible, "`metric-1`");

        var single = ReportRenderer.Render(delta, assessment, null, ReportFormat.Comment, new OutputLimits(Findings: 7));
        Contains(single, "1 more finding is not shown.");
    }

    private static void CommentKeepsToTheLineLimit()
    {
        var findings = Enumerable.Range(1, 12)
            .Select(i => Observed($"m{i:00}", "fail", "1", "2", "1", "1"))
            .ToArray();
        var (delta, assessment) = Documents(findings, "fail");
        foreach (var lines in new[] { 8, 10, 25 })
        {
            var comment = ReportRenderer.Render(delta, assessment, null, ReportFormat.Comment, new OutputLimits(Lines: lines, Findings: 50));
            // The summary line of the collapsed section is visible, so it counts.
            var details = comment.IndexOf("<details>", StringComparison.Ordinal);
            var outside = details < 0
                ? comment.TrimEnd('\n').Split('\n').Length
                : comment[..details].TrimEnd('\n').Split('\n').Length + 1;
            if (outside > lines)
            {
                throw new InvalidOperationException($"The comment used {outside} lines outside the collapsed section; the limit is {lines}.");
            }
        }
    }

    private static void CommentKeepsToTheColumnLimit()
    {
        var (delta, assessment) = Documents([Observed("size", "warn", "1", "2", "1", "1")], "warn");
        var comment = ReportRenderer.Render(delta, assessment, null, ReportFormat.Comment, new OutputLimits(Columns: 2));
        Contains(comment, "| Finding | Before |\n| --- | --- |\n| **warn** `size` | `1` |\n");
        DoesNotContain(comment, "After");
    }

    private static void CommentEscapesUntrustedText()
    {
        var (delta, assessment) = Documents([Observed("a|b`c\nd", "warn", "1", "2", "1", "1")], "warn");
        var comment = ReportRenderer.Render(delta, assessment, ReportFormat.Comment);
        Contains(comment, "`a\\|b'c d`");
        var agent = ReportRenderer.Render(delta, assessment, ReportFormat.Agent);
        Equal(4, agent.Split('\n').Length, "A line break in a finding must not start a new agent line.");
    }

    private static void AgentFormatStaysWithinTokenBudget()
    {
        var findings = Enumerable.Range(1, 60)
            .Select(i => Observed($"metric-with-a-long-name-{i:00}", "warn", "1000", "2000", "1000", "1"))
            .ToArray();
        var (delta, assessment) = Documents(findings, "warn");
        var agent = ReportRenderer.Render(delta, assessment, ReportFormat.Agent);
        if (agent.Length > OutputLimits.Default.AgentTokens * 4)
        {
            throw new InvalidOperationException($"The agent file has {agent.Length} characters; the budget is {OutputLimits.Default.AgentTokens * 4}.");
        }

        Contains(agent, "attention: review\nfindings: 60\n");
        Contains(agent, "- review warn metric-with-a-long-name-01 before=1000 after=2000 change=+1000 (+100%): ");
        var written = agent.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal));
        Contains(agent, $"truncated: {60 - written} more findings are in the full report\n");
    }

    private static void AgentFormatStatesLevelWithoutAssessment()
    {
        var delta = ChangedDelta();
        var agent = ReportRenderer.Render(delta, null, ReportFormat.Agent);
        Contains(agent, "attention: review\nfindings: 2\n");
        Contains(agent, "- review changed /metrics/a before=0 after=5 change=+5 (from zero): '/metrics/a' is modified; no rule judged it.\n");
        var comment = ReportRenderer.Render(delta, null, ReportFormat.Comment);
        Contains(comment, "### Worth reviewing");
    }

    private static void UncomparableDeltaIsWorthReviewing()
    {
        var delta = Bytes(SetId(
            Delta("\"status\":\"incompatible\",\"changes\":[],\"message\":\"Methodology differs.\""),
            "delta"));
        var agent = ReportRenderer.Render(delta, null, ReportFormat.Agent);
        Equal("attention: review\nfindings: 1\n- review inconclusive comparison: Methodology differs.\n", agent, "An uncomparable Delta is worth reviewing.");
    }

    private static void OutputIsDeterministic()
    {
        var (delta, assessment) = Documents(
        [
            Observed("b", "warn", "1", "2", "1", "1"),
            Observed("a", "warn", "1", "2", "1", "1"),
        ], "warn");
        var first = ReportRenderer.Render(delta, assessment, ReportFormat.Comment);
        Equal(first, ReportRenderer.Render(delta, assessment, ReportFormat.Comment), "Repeated comment rendering changed bytes.");
        if (first.IndexOf("`a`", StringComparison.Ordinal) > first.IndexOf("`b`", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Equal findings must fall back to ID order.");
        }
    }

    private static void LimitsAreValidated()
    {
        var delta = ChangedDelta();
        Throws<ArgumentOutOfRangeException>(() => ReportRenderer.Render(delta, null, null, ReportFormat.Comment, new OutputLimits(Lines: 7)));
        Throws<ArgumentOutOfRangeException>(() => ReportRenderer.Render(delta, null, null, ReportFormat.Comment, new OutputLimits(Findings: 0)));
        Throws<ArgumentOutOfRangeException>(() => ReportRenderer.Render(delta, null, null, ReportFormat.Comment, new OutputLimits(Columns: 0)));
        Throws<ArgumentOutOfRangeException>(() => ReportRenderer.Render(delta, null, null, ReportFormat.Agent, new OutputLimits(AgentTokens: 49)));
        Throws<ArgumentNullException>(() => ReportRenderer.Render(delta, null, null, ReportFormat.Agent, null!));
    }

    private static RankedFinding Finding(string status, string id, string? relative = "0") =>
        new(Attention.ForStatus(status), status, id, "message", null, null, null, relative);

    private static string Observed(string id, string status, string before, string after, string absolute, string? relative)
    {
        var observed = new Dictionary<string, string?> { ["before"] = before, ["after"] = after, ["absolute"] = absolute, ["relative"] = relative };
        return JsonSerializer.Serialize(new
        {
            id,
            status,
            message = $"Rule {status} for {id}.",
            changePaths = ChangePaths,
            observed,
        });
    }

    private static string Delta(string body) => $$$"""
        {"schema":"https://diffra.dev/schemas/delta-v1.schema.json","id":"{{{Placeholder}}}",{{{body}}},"typeResolution":"recognized","baselineId":"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","candidateEvidenceId":"sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","comparator":{"id":"diffra.numeric","version":"1.0.0","configDigest":"sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","inputTypeId":"org.example.metrics","inputTypeVersion":"1.0"},"provenance":{"baselineEvidenceId":"sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","candidateEvidenceId":"sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","environment":{} } }
        """;

    private static byte[] ChangedDelta() => Bytes(SetId(
        Delta("\"status\":\"changed\",\"changes\":[" +
            "{\"path\":\"/metrics/a\",\"kind\":\"modified\",\"value\":{\"before\":\"0\",\"after\":\"5\",\"absolute\":\"5\"}}," +
            "{\"path\":\"/metrics/b\",\"kind\":\"modified\",\"value\":{\"before\":\"10\",\"after\":\"11\",\"absolute\":\"1\",\"relative\":\"0.1\"}}]"),
        "delta"));

    private static byte[] UnchangedDelta() => Bytes(SetId(Delta("\"status\":\"unchanged\",\"changes\":[]"), "delta"));

    private static (byte[] Delta, byte[] Assessment) Documents(IEnumerable<string> findings, string outcome)
    {
        var delta = ChangedDelta();
        var deltaId = JsonDocument.Parse(delta).RootElement.GetProperty("id").GetString()!;
        var assessment = $$$"""
            {"schema":"https://diffra.dev/schemas/assessment-v1.schema.json","id":"{{{Placeholder}}}","deltaId":"{{{deltaId}}}","deltaStatus":"changed","policy":{"id":"org.example.policy","version":"1.0.0","rulesDigest":"sha256:1111111111111111111111111111111111111111111111111111111111111111"},"outcome":"{{{outcome}}}","findings":[{{{string.Join(',', findings)}}}],"provenance":{"evaluator":"diffra.policy","version":"1.0.0","inputDeltaId":"{{{deltaId}}}"}}
            """;
        return (delta, Bytes(SetId(assessment, "assessment")));
    }

    private static int CountRows(string markdown) =>
        markdown.Split('\n').Count(line => line.StartsWith("| **", StringComparison.Ordinal));

    private static string SetId(string json, string kind)
    {
        var id = DocumentIdentity.ComputeId(kind, Bytes(json));
        return json.Replace(Placeholder, id, StringComparison.Ordinal);
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static void Contains(string value, string expected)
    {
        if (!value.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected output to contain: {expected}\nActual:\n{value}");
        }
    }

    private static void DoesNotContain(string value, string unexpected)
    {
        if (value.Contains(unexpected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected output not to contain: {unexpected}\nActual:\n{value}");
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

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
