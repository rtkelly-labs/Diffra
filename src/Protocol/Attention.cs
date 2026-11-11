using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Diffra.Protocol;

/// <summary>
/// The single headline for a result. It comes from rule outcomes only and carries no size or
/// magnitude. The order is significant: a higher value needs more attention.
/// </summary>
public enum AttentionLevel
{
    Quiet,
    Review,
    Act,
}

/// <summary>Limits that keep the pull request comment and the agent-facing Markdown to one screen.</summary>
/// <param name="Lines">Rendered comment lines outside collapsed sections.</param>
/// <param name="Findings">Findings shown before the overflow line.</param>
/// <param name="Columns">Columns in any comment table.</param>
/// <param name="AgentTokens">Approximate tokens in the agent-facing Markdown, estimated as one per four characters.</param>
public sealed record OutputLimits(int Lines = 25, int Findings = 5, int Columns = 4, int AgentTokens = 500)
{
    /// <summary>The default one-screen limits.</summary>
    public static OutputLimits Default { get; } = new();

    internal void Validate()
    {
        if (Lines < 8) throw new ArgumentOutOfRangeException(nameof(Lines), Lines, "The comment needs at least 8 lines.");
        if (Findings < 1) throw new ArgumentOutOfRangeException(nameof(Findings), Findings, "At least one finding must be shown.");
        if (Columns < 1) throw new ArgumentOutOfRangeException(nameof(Columns), Columns, "A table needs at least one column.");
        if (AgentTokens < 50) throw new ArgumentOutOfRangeException(nameof(AgentTokens), AgentTokens, "The agent-facing Markdown needs at least 50 tokens.");
    }
}

/// <summary>One finding as it is ranked and shown.</summary>
public sealed record RankedFinding(
    AttentionLevel Level,
    string Status,
    string Id,
    string Message,
    string? Before,
    string? After,
    string? Absolute,
    string? Relative);

/// <summary>Maps rule outcomes to attention levels and orders findings for display.</summary>
public static class Attention
{
    /// <summary>The stable token for agents and settings: <c>act</c>, <c>review</c> or <c>quiet</c>.</summary>
    public static string Token(AttentionLevel level) => level switch
    {
        AttentionLevel.Act => "act",
        AttentionLevel.Review => "review",
        AttentionLevel.Quiet => "quiet",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown attention level."),
    };

    /// <summary>The label shown to people.</summary>
    public static string Label(AttentionLevel level) => level switch
    {
        AttentionLevel.Act => "Needs action",
        AttentionLevel.Review => "Worth reviewing",
        AttentionLevel.Quiet => "Quiet",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown attention level."),
    };

    /// <summary>
    /// The level for one rule outcome. A failed rule needs action. A warning, a value that needs
    /// review and a comparison that could not be judged are worth reviewing. Only a pass is quiet,
    /// so an unknown outcome is never shown as one.
    /// </summary>
    public static AttentionLevel ForStatus(string status) => status switch
    {
        "fail" => AttentionLevel.Act,
        "pass" => AttentionLevel.Quiet,
        _ => AttentionLevel.Review,
    };

    /// <summary>The level of a whole result: the highest level of any finding.</summary>
    public static AttentionLevel ForFindings(IEnumerable<RankedFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return findings.Select(f => f.Level).DefaultIfEmpty(AttentionLevel.Quiet).Max();
    }

    /// <summary>
    /// Orders findings by attention level, then rule severity, then the size of the relative change.
    /// Ties fall back to the finding ID so the order is deterministic.
    /// </summary>
    public static IReadOnlyList<RankedFinding> Rank(IEnumerable<RankedFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return findings
            .OrderByDescending(f => f.Level)
            .ThenByDescending(f => SeverityRank(f.Status))
            .ThenByDescending(f => RelativeMagnitude(f.Relative))
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .ToList();
    }

    // The same order the assessment uses for its overall outcome.
    private static int SeverityRank(string status) => status switch
    {
        "fail" => 5,
        "inconclusive" => 4,
        "needs-review" => 3,
        "warn" => 2,
        "changed" => 1,
        _ => 0,
    };

    // An undefined relative change (a zero starting value) ranks above every defined one.
    private static decimal RelativeMagnitude(string? relative) =>
        relative is null ? decimal.MaxValue
        : decimal.TryParse(relative, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? Math.Abs(value)
        : 0m;

    /// <summary>
    /// Reads the findings of a validated Delta and optional Assessment. With an Assessment, each of
    /// its findings is used. Without one, each change is a finding worth reviewing, because no rule
    /// judged it, and a Delta that could not be compared is one finding worth reviewing.
    /// </summary>
    internal static IReadOnlyList<RankedFinding> Read(JsonElement delta, JsonElement? assessment)
    {
        var findings = new List<RankedFinding>();
        if (assessment is { } assessed)
        {
            foreach (var finding in assessed.GetProperty("findings").EnumerateArray())
            {
                var status = finding.GetProperty("status").GetString()!;
                var observed = finding.TryGetProperty("observed", out var value) && value.ValueKind == JsonValueKind.Object ? value : (JsonElement?)null;
                findings.Add(new RankedFinding(
                    ForStatus(status),
                    status,
                    finding.GetProperty("id").GetString()!,
                    finding.GetProperty("message").GetString()!,
                    Text(observed, "before"),
                    Text(observed, "after"),
                    Text(observed, "absolute"),
                    Text(observed, "relative")));
            }

            // An outcome that no finding explains still sets the level, so a failed or inconclusive
            // assessment is never shown as quiet.
            var outcome = assessed.GetProperty("outcome").GetString()!;
            if (ForStatus(outcome) > ForFindings(findings))
            {
                findings.Add(new RankedFinding(
                    ForStatus(outcome),
                    outcome,
                    "assessment",
                    $"The assessment outcome is {outcome}, but no finding explains it.",
                    null,
                    null,
                    null,
                    null));
            }

            return Rank(findings);
        }

        var deltaStatus = delta.GetProperty("status").GetString()!;
        if (deltaStatus == "unchanged")
        {
            return [];
        }

        if (deltaStatus != "changed")
        {
            var message = delta.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()!
                : $"The comparison is {deltaStatus} and cannot be judged.";
            return [new RankedFinding(AttentionLevel.Review, "inconclusive", "comparison", message, null, null, null, null)];
        }

        foreach (var change in delta.GetProperty("changes").EnumerateArray())
        {
            var path = change.GetProperty("path").GetString()!;
            var kind = change.GetProperty("kind").GetString()!;
            var value = change.GetProperty("value");
            findings.Add(new RankedFinding(
                AttentionLevel.Review,
                "changed",
                path,
                $"'{path}' is {kind}; no rule judged it.",
                Text(value, "before"),
                Text(value, "after"),
                Text(value, "absolute"),
                Text(value, "relative")));
        }

        return Rank(findings);
    }

    private static string? Text(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(property, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
}

/// <summary>Renders the one-screen pull request comment and the agent-facing Markdown.</summary>
internal static class AttentionRenderer
{
    private static readonly string[] Headings = ["Finding", "Before", "After", "Change"];

    private const int MinimumClippedLine = 80;

    // Well inside decimal range once multiplied by 100.
    private const decimal MaxRelative = 1_000_000_000_000m;

    /// <summary>
    /// The pull request comment. It is empty when the result is quiet, because nothing is posted on
    /// a quiet pull request. Findings beyond the limits become one overflow line, and the full list
    /// follows in a collapsed section.
    /// </summary>
    public static string RenderComment(IReadOnlyList<RankedFinding> findings, OutputLimits limits)
    {
        var level = Attention.ForFindings(findings);
        if (level == AttentionLevel.Quiet)
        {
            return string.Empty;
        }

        var shown = findings.Where(f => f.Level != AttentionLevel.Quiet).ToList();
        var columns = Math.Min(limits.Columns, Headings.Length);

        // Heading, blank line and the two table header lines. An overflow adds a blank line, the
        // overflow line and the summary line of the collapsed section, whose contents do not count.
        const int fixedLines = 4;
        const int overflowLines = 3;
        var room = shown.Count <= limits.Findings && shown.Count <= limits.Lines - fixedLines
            ? shown.Count
            : limits.Lines - fixedLines - overflowLines;
        var visible = Math.Min(Math.Min(limits.Findings, room), shown.Count);

        var builder = new StringBuilder();
        builder.Append("### ").Append(Attention.Label(level)).Append('\n').Append('\n');
        AppendTable(builder, shown.Take(visible), columns);
        if (visible < shown.Count)
        {
            var hidden = shown.Count - visible;
            builder.Append('\n')
                .Append(hidden).Append(hidden == 1 ? " more finding is" : " more findings are")
                .Append(" not shown.\n")
                .Append("<details><summary>All ").Append(shown.Count).Append(" findings</summary>\n\n");
            AppendTable(builder, shown, columns);
            builder.Append("\n</details>\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// The agent-facing Markdown: the level, a count, and one line per finding while the estimated
    /// token count stays within the limit. A truncation is stated in one line.
    /// </summary>
    public static string RenderAgent(IReadOnlyList<RankedFinding> findings, OutputLimits limits)
    {
        var level = Attention.ForFindings(findings);
        var shown = findings.Where(f => f.Level != AttentionLevel.Quiet).ToList();
        var builder = new StringBuilder();
        builder.Append("attention: ").Append(Attention.Token(level)).Append('\n')
            .Append("findings: ").Append(shown.Count).Append('\n');

        // Keep room for the truncation line, so the whole file stays within the limit.
        const int truncationReserve = 20;
        var budget = (limits.AgentTokens - truncationReserve) * 4;
        var written = 0;
        foreach (var finding in shown)
        {
            var line = AgentLine(finding);
            var remaining = budget - builder.Length;
            if (line.Length > remaining)
            {
                // A long line is shortened rather than dropped when enough room is left for it to
                // say something, so one long message cannot hide every finding.
                if (remaining >= MinimumClippedLine)
                {
                    builder.Append(line.AsSpan(0, remaining - 2)).Append("…\n");
                    written++;
                }

                break;
            }

            builder.Append(line);
            written++;
        }

        if (written < shown.Count)
        {
            var hidden = shown.Count - written;
            builder.Append("truncated: ").Append(hidden).Append(hidden == 1 ? " more finding is" : " more findings are").Append(" in the full report\n");
        }

        return builder.ToString();
    }

    private static string AgentLine(RankedFinding finding)
    {
        var line = new StringBuilder();
        line.Append("- ").Append(Attention.Token(finding.Level)).Append(' ').Append(finding.Status).Append(' ').Append(OneLine(finding.Id));
        if (finding.Before is not null || finding.After is not null)
        {
            line.Append(" before=").Append(OneLine(finding.Before ?? "none")).Append(" after=").Append(OneLine(finding.After ?? "none"));
        }

        var change = Change(finding);
        if (change.Length > 0)
        {
            line.Append(" change=").Append(OneLine(change));
        }

        line.Append(": ").Append(OneLine(finding.Message)).Append('\n');
        return line.ToString();
    }

    private static void AppendTable(StringBuilder builder, IEnumerable<RankedFinding> rows, int columns)
    {
        builder.Append('|');
        for (var i = 0; i < columns; i++) builder.Append(' ').Append(Headings[i]).Append(" |");
        builder.Append("\n|");
        for (var i = 0; i < columns; i++) builder.Append(" --- |");
        builder.Append('\n');

        foreach (var finding in rows)
        {
            string[] cells =
            [
                // A finding with no values to show, such as one that could not be compared, keeps
                // its explanation so the reader can see why it needs attention.
                finding.Before is null && finding.After is null
                    ? $"**{finding.Status}** `{Code(finding.Id)}`: {Prose(finding.Message)}"
                    : $"**{finding.Status}** `{Code(finding.Id)}`",
                finding.Before is null ? string.Empty : $"`{Code(finding.Before)}`",
                finding.After is null ? string.Empty : $"`{Code(finding.After)}`",
                Change(finding) is { Length: > 0 } change ? $"`{Code(change)}`" : string.Empty,
            ];
            builder.Append('|');
            for (var i = 0; i < columns; i++) builder.Append(' ').Append(cells[i]).Append(" |");
            builder.Append('\n');
        }
    }

    private static string Change(RankedFinding finding)
    {
        if (finding.Absolute is null)
        {
            return string.Empty;
        }

        var absolute = finding.Absolute.StartsWith('-') ? finding.Absolute : "+" + finding.Absolute;
        if (finding.Relative is null)
        {
            return finding.Before is "0" ? absolute + " (from zero)" : absolute;
        }

        // The relative value comes from the input and is not validated, so a value that is not a plain
        // decimal, or too large to show as a percentage, leaves the absolute change on its own.
        if (!decimal.TryParse(finding.Relative, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var relative)
            || Math.Abs(relative) > MaxRelative)
        {
            return absolute;
        }

        var percent = relative * 100m;
        var rounded = Math.Round(percent, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture);
        return $"{absolute} ({(percent >= 0 ? "+" : string.Empty)}{rounded}%)";
    }

    // Text inside a code span: no line breaks, backticks or table separators.
    private static string Code(string value) =>
        OneLine(value).Replace('`', '\'').Replace("|", "\\|", StringComparison.Ordinal);

    private static string Prose(string value) =>
        OneLine(value).Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);

    private static string OneLine(string value) =>
        value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
