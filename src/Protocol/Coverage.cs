using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Diffra.Protocol;

/// <summary>Represents a deterministic aggregate counter for code coverage.</summary>
public sealed record CoverageCounter(int Total, int Covered, int Missed, double Rate)
{
    public static CoverageCounter Create(int total, int covered)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        ArgumentOutOfRangeException.ThrowIfNegative(covered);
        if (covered > total)
        {
            throw new ArgumentException("Covered count cannot exceed total count.", nameof(covered));
        }

        var missed = total - covered;
        var rate = total == 0 ? 1.0 : Math.Round((double)covered / total, 4);
        return new CoverageCounter(total, covered, missed, rate);
    }
}

/// <summary>Represents per-file code coverage metrics with canonical interval encoding.</summary>
public sealed record FileCoverage(
    string Path,
    CoverageCounter Lines,
    CoverageCounter Branches,
    string UncoveredLineRanges);

/// <summary>Canonical typed code coverage payload conforming to coverage-v1.schema.json.</summary>
public sealed record CoveragePayload(
    string Scope,
    string Methodology,
    CoverageCounter Lines,
    CoverageCounter Branches,
    CoverageCounter Methods,
    IReadOnlyList<FileCoverage> Files)
{
    public const string SchemaUri = "https://diffra.dev/schemas/coverage-v1.schema.json";

    public JsonObject ToJsonObject()
    {
        var sortedFiles = Files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var filesArray = new JsonArray();
        foreach (var file in sortedFiles)
        {
            filesArray.Add(new JsonObject
            {
                ["path"] = file.Path,
                ["lines"] = ToCounterNode(file.Lines),
                ["branches"] = ToCounterNode(file.Branches),
                ["uncoveredLineRanges"] = file.UncoveredLineRanges,
            });
        }

        return new JsonObject
        {
            ["schema"] = SchemaUri,
            ["scope"] = Scope,
            ["methodology"] = Methodology,
            ["summary"] = new JsonObject
            {
                ["lines"] = ToCounterNode(Lines),
                ["branches"] = ToCounterNode(Branches),
                ["methods"] = ToCounterNode(Methods),
            },
            ["files"] = filesArray,
        };
    }

    public static CoveragePayload FromJsonElement(JsonElement root)
    {
        var scope = root.GetProperty("scope").GetString()!;
        var methodology = root.GetProperty("methodology").GetString()!;

        var summary = root.GetProperty("summary");
        var lines = ParseCounter(summary.GetProperty("lines"));
        var branches = ParseCounter(summary.GetProperty("branches"));
        var methods = ParseCounter(summary.GetProperty("methods"));

        var files = new List<FileCoverage>();
        foreach (var item in root.GetProperty("files").EnumerateArray())
        {
            var path = item.GetProperty("path").GetString()!;
            var fileLines = ParseCounter(item.GetProperty("lines"));
            var fileBranches = ParseCounter(item.GetProperty("branches"));
            var uncovered = item.GetProperty("uncoveredLineRanges").GetString()!;
            files.Add(new FileCoverage(path, fileLines, fileBranches, uncovered));
        }

        return new CoveragePayload(scope, methodology, lines, branches, methods, files);
    }

    /// <summary>Compresses an arbitrary sequence of line numbers into canonical intervals, e.g. '14-16, 25'.</summary>
    public static string FormatLineRanges(IEnumerable<int> lineNumbers)
    {
        var sorted = lineNumbers.Distinct().Where(n => n > 0).OrderBy(n => n).ToList();
        if (sorted.Count == 0) return string.Empty;

        var ranges = new List<string>();
        int start = sorted[0];
        int prev = start;

        for (int i = 1; i < sorted.Count; i++)
        {
            int current = sorted[i];
            if (current == prev + 1)
            {
                prev = current;
            }
            else
            {
                ranges.Add(start == prev ? start.ToString(CultureInfo.InvariantCulture) : $"{start}-{prev}");
                start = current;
                prev = current;
            }
        }
        ranges.Add(start == prev ? start.ToString(CultureInfo.InvariantCulture) : $"{start}-{prev}");
        return string.Join(", ", ranges);
    }

    /// <summary>Expands a canonical interval string (e.g. '14-16, 25') back into a set of line numbers.</summary>
    public static HashSet<int> ParseLineRanges(string intervals)
    {
        var result = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(intervals)) return result;

        foreach (var segment in intervals.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = segment.Split('-');
            if (parts.Length == 1 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var single))
            {
                result.Add(single);
            }
            else if (parts.Length == 2 &&
                     int.TryParse(parts[0], CultureInfo.InvariantCulture, out var start) &&
                     int.TryParse(parts[1], CultureInfo.InvariantCulture, out var end))
            {
                for (int n = start; n <= end; n++) result.Add(n);
            }
        }
        return result;
    }

    private static JsonObject ToCounterNode(CoverageCounter counter) => new()
    {
        ["total"] = counter.Total,
        ["covered"] = counter.Covered,
        ["missed"] = counter.Missed,
        ["rate"] = counter.Rate,
    };

    private static CoverageCounter ParseCounter(JsonElement elem) => new(
        elem.GetProperty("total").GetInt32(),
        elem.GetProperty("covered").GetInt32(),
        elem.GetProperty("missed").GetInt32(),
        elem.GetProperty("rate").GetDouble());
}

/// <summary>Detailed delta comparison between baseline and candidate coverage payloads.</summary>
public sealed record CoverageDeltaResult(
    double LineRateDelta,
    double BranchRateDelta,
    int TotalCoveredLinesDelta,
    int TotalMissedLinesDelta,
    IReadOnlyList<NewlyUncoveredLinesFinding> NewlyUncoveredLines,
    IReadOnlyList<FileCoverageDelta> FileDeltas);

public sealed record NewlyUncoveredLinesFinding(
    string Path,
    IReadOnlyList<int> LineNumbers,
    string FormattedRange);

public sealed record FileCoverageDelta(
    string Path,
    double LineRateDelta,
    double BranchRateDelta);

/// <summary>Evaluates policy rules against coverage delta.</summary>
public sealed record CoveragePolicy(
    double MinOverallLineRate,
    double MaxAllowedOverallDrop,
    bool FailOnNewlyUncoveredLines);

public static class CoverageEvaluator
{
    public static CoverageDeltaResult Compare(CoveragePayload baseline, CoveragePayload candidate)
    {
        var lineRateDelta = Math.Round(candidate.Lines.Rate - baseline.Lines.Rate, 4);
        var branchRateDelta = Math.Round(candidate.Branches.Rate - baseline.Branches.Rate, 4);
        var coveredLinesDelta = candidate.Lines.Covered - baseline.Lines.Covered;
        var missedLinesDelta = candidate.Lines.Missed - baseline.Lines.Missed;

        var baselineFiles = baseline.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var newlyUncoveredList = new List<NewlyUncoveredLinesFinding>();
        var fileDeltas = new List<FileCoverageDelta>();

        foreach (var candFile in candidate.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var candUncovered = CoveragePayload.ParseLineRanges(candFile.UncoveredLineRanges);
            if (baselineFiles.TryGetValue(candFile.Path, out var baseFile))
            {
                var baseUncovered = CoveragePayload.ParseLineRanges(baseFile.UncoveredLineRanges);
                var newlyMissed = candUncovered.Where(line => !baseUncovered.Contains(line)).OrderBy(l => l).ToList();
                if (newlyMissed.Count > 0)
                {
                    newlyUncoveredList.Add(new NewlyUncoveredLinesFinding(
                        candFile.Path,
                        newlyMissed,
                        CoveragePayload.FormatLineRanges(newlyMissed)));
                }

                fileDeltas.Add(new FileCoverageDelta(
                    candFile.Path,
                    Math.Round(candFile.Lines.Rate - baseFile.Lines.Rate, 4),
                    Math.Round(candFile.Branches.Rate - baseFile.Branches.Rate, 4)));
            }
            else
            {
                // New file in candidate
                if (candUncovered.Count > 0)
                {
                    newlyUncoveredList.Add(new NewlyUncoveredLinesFinding(
                        candFile.Path,
                        candUncovered.OrderBy(l => l).ToList(),
                        candFile.UncoveredLineRanges));
                }

                fileDeltas.Add(new FileCoverageDelta(
                    candFile.Path,
                    candFile.Lines.Rate,
                    candFile.Branches.Rate));
            }
        }

        return new CoverageDeltaResult(
            lineRateDelta,
            branchRateDelta,
            coveredLinesDelta,
            missedLinesDelta,
            newlyUncoveredList,
            fileDeltas);
    }

    public static (string Outcome, IReadOnlyList<AssessmentFinding> Findings) Evaluate(
        CoveragePayload candidate,
        CoverageDeltaResult delta,
        CoveragePolicy policy)
    {
        var findings = new List<AssessmentFinding>();
        var outcome = "pass";

        // 1. Min overall line rate
        if (candidate.Lines.Rate < policy.MinOverallLineRate)
        {
            outcome = "fail";
            findings.Add(new AssessmentFinding(
                "coverage/minimum-threshold",
                "fail",
                $"Overall line coverage {candidate.Lines.Rate * 100:F1}% is below the required minimum of {policy.MinOverallLineRate * 100:F1}%.",
                ["summary.lines.rate"],
                candidate.Lines.Rate,
                policy.MinOverallLineRate));
        }

        // 2. Max allowed overall drop
        var drop = -delta.LineRateDelta;
        if (drop > policy.MaxAllowedOverallDrop)
        {
            outcome = "fail";
            findings.Add(new AssessmentFinding(
                "coverage/regression-drop",
                "fail",
                $"Line coverage decreased by {drop * 100:F2}%, which exceeds the permitted drop budget of {policy.MaxAllowedOverallDrop * 100:F2}%.",
                ["summary.lines.rate"],
                candidate.Lines.Rate,
                candidate.Lines.Rate + drop));
        }

        // 3. Newly uncovered lines
        if (delta.NewlyUncoveredLines.Count > 0)
        {
            var newlyMissedSeverity = policy.FailOnNewlyUncoveredLines ? "fail" : "warn";
            if (newlyMissedSeverity == "fail") outcome = "fail";
            else if (outcome == "pass") outcome = "warn";

            foreach (var finding in delta.NewlyUncoveredLines)
            {
                findings.Add(new AssessmentFinding(
                    "coverage/newly-uncovered-lines",
                    newlyMissedSeverity,
                    $"Newly uncovered lines introduced in '{finding.Path}': {finding.FormattedRange}.",
                    [finding.Path],
                    finding.FormattedRange,
                    null));
            }
        }

        if (findings.Count == 0)
        {
            findings.Add(new AssessmentFinding(
                "coverage/policy",
                "pass",
                $"Code coverage meets all policy standards (Line: {candidate.Lines.Rate * 100:F1}%, Delta: {(delta.LineRateDelta >= 0 ? "+" : "")}{delta.LineRateDelta * 100:F2}%).",
                [],
                candidate.Lines.Rate,
                null));
        }

        return (outcome, findings);
    }
}
