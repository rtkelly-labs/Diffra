using System.Text.Json;
using Diffra.Protocol;

namespace Diffra.Protocol.Tests;

public static class TimeSeriesGraphTests
{
    public static void RunAll()
    {
        SerializesAndDeserializesTimeSeriesProjection();
        RendersAsciiSparklinesCorrectly();
        RendersSvgSparklinesCorrectly();
        RendersSvgEvolutionChartCorrectly();
        Console.WriteLine("PASS time-series projections, ascii sparklines, and design-system SVG charts");
    }

    private static void SerializesAndDeserializesTimeSeriesProjection()
    {
        var now = DateTimeOffset.UtcNow;
        var p1 = new TimeSeriesPoint("1111111111111111111111111111111111111111", now.AddDays(-2), 85.0, "v0.1.0");
        var p2 = new TimeSeriesPoint("2222222222222222222222222222222222222222", now.AddDays(-1), 88.5);
        var cand = new CandidatePoint("3333333333333333333333333333333333333333", 91.0, 2.5);
        var thresh = new TimeSeriesThresholds(Min: 80.0, Max: 100.0);

        var projection = new TimeSeriesProjection(
            "diffra.coverage.lines.rate",
            "diffra/core",
            "percent",
            [p1, p2],
            cand,
            thresh);

        var json = projection.ToJsonObject().ToJsonString();
        using var doc = JsonDocument.Parse(json);
        var roundtripped = TimeSeriesProjection.FromJsonElement(doc.RootElement);

        Equal("diffra.coverage.lines.rate", roundtripped.MetricId);
        Equal("diffra/core", roundtripped.SubjectId);
        Equal("percent", roundtripped.Unit);
        Equal(2, roundtripped.Points.Count);
        Equal("v0.1.0", roundtripped.Points[0].Tag);
        Equal(91.0, roundtripped.Candidate?.Value);
        Equal(2.5, roundtripped.Candidate?.Delta);
        Equal(80.0, roundtripped.Thresholds?.Min);
    }

    private static void RendersAsciiSparklinesCorrectly()
    {
        Equal(string.Empty, TimeSeriesGraph.RenderAsciiSparkline([]));
        Equal("▄", TimeSeriesGraph.RenderAsciiSparkline([42.0]));
        Equal("▄▄▄", TimeSeriesGraph.RenderAsciiSparkline([10.0, 10.0, 10.0]));

        var spark = TimeSeriesGraph.RenderAsciiSparkline([0.0, 25.0, 50.0, 75.0, 100.0]);
        Equal(" ▃▅▆█", spark);
    }

    private static void RendersSvgSparklinesCorrectly()
    {
        var svg = TimeSeriesGraph.RenderSvgSparkline([10.0, 15.0, 20.0, 18.0]);
        if (!svg.StartsWith("<svg", StringComparison.Ordinal) || !svg.Contains("<polyline", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Invalid SVG sparkline generated.");
        }
        if (!svg.Contains("--ds-intent-info", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SVG sparkline does not reference design system token.");
        }
    }

    private static void RendersSvgEvolutionChartCorrectly()
    {
        var now = DateTimeOffset.UtcNow;
        var p1 = new TimeSeriesPoint("1111111111111111111111111111111111111111", now.AddDays(-2), 85.0, "v0.1.0");
        var p2 = new TimeSeriesPoint("2222222222222222222222222222222222222222", now.AddDays(-1), 88.5);
        var cand = new CandidatePoint("3333333333333333333333333333333333333333", 91.0, 2.5);
        var thresh = new TimeSeriesThresholds(Min: 80.0);

        var projection = new TimeSeriesProjection(
            "diffra.coverage.lines.rate",
            "diffra/core",
            "percent",
            [p1, p2],
            cand,
            thresh);

        var chartSvg = TimeSeriesGraph.RenderSvgEvolutionChart(projection);
        if (!chartSvg.StartsWith("<svg", StringComparison.Ordinal) || !chartSvg.EndsWith("</svg>", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Invalid SVG evolution chart root tag.");
        }

        if (!chartSvg.Contains("Min: 80.0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Threshold line missing from SVG evolution chart.");
        }

        if (!chartSvg.Contains("PR", StringComparison.Ordinal) || !chartSvg.Contains("+2.5", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Candidate PR badge missing from SVG evolution chart.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected: {expected}, Actual: {actual}");
        }
    }
}
