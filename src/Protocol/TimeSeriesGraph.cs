using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Diffra.Protocol;

public sealed record TimeSeriesPoint(
    string Commit,
    DateTimeOffset Timestamp,
    double Value,
    string? Tag = null);

public sealed record CandidatePoint(
    string Commit,
    double Value,
    double Delta);

public sealed record TimeSeriesThresholds(
    double? Min = null,
    double? Max = null,
    double? Target = null);

public sealed record TimeSeriesProjection(
    string MetricId,
    string SubjectId,
    string Unit,
    IReadOnlyList<TimeSeriesPoint> Points,
    CandidatePoint? Candidate = null,
    TimeSeriesThresholds? Thresholds = null)
{
    public const string SchemaUri = "https://diffra.dev/schemas/timeseries-v1.schema.json";

    public JsonObject ToJsonObject()
    {
        var pointsArray = new JsonArray();
        foreach (var p in Points.OrderBy(p => p.Timestamp))
        {
            var obj = new JsonObject
            {
                ["commit"] = p.Commit,
                ["timestamp"] = p.Timestamp.ToString("O"),
                ["value"] = p.Value,
            };
            if (!string.IsNullOrEmpty(p.Tag)) obj["tag"] = p.Tag;
            pointsArray.Add(obj);
        }

        var root = new JsonObject
        {
            ["schema"] = SchemaUri,
            ["metricId"] = MetricId,
            ["subjectId"] = SubjectId,
            ["unit"] = Unit,
            ["points"] = pointsArray,
        };

        if (Candidate != null)
        {
            root["candidate"] = new JsonObject
            {
                ["commit"] = Candidate.Commit,
                ["value"] = Candidate.Value,
                ["delta"] = Candidate.Delta,
            };
        }

        if (Thresholds != null)
        {
            var threshObj = new JsonObject();
            if (Thresholds.Min.HasValue) threshObj["min"] = Thresholds.Min.Value;
            if (Thresholds.Max.HasValue) threshObj["max"] = Thresholds.Max.Value;
            if (Thresholds.Target.HasValue) threshObj["target"] = Thresholds.Target.Value;
            root["thresholds"] = threshObj;
        }

        return root;
    }

    public static TimeSeriesProjection FromJsonElement(JsonElement root)
    {
        var metricId = root.GetProperty("metricId").GetString()!;
        var subjectId = root.GetProperty("subjectId").GetString()!;
        var unit = root.GetProperty("unit").GetString()!;

        var points = new List<TimeSeriesPoint>();
        foreach (var item in root.GetProperty("points").EnumerateArray())
        {
            var commit = item.GetProperty("commit").GetString()!;
            var ts = item.GetProperty("timestamp").GetDateTimeOffset();
            var val = item.GetProperty("value").GetDouble();
            var tag = item.TryGetProperty("tag", out var t) ? t.GetString() : null;
            points.Add(new TimeSeriesPoint(commit, ts, val, tag));
        }

        CandidatePoint? candidate = null;
        if (root.TryGetProperty("candidate", out var candElem))
        {
            candidate = new CandidatePoint(
                candElem.GetProperty("commit").GetString()!,
                candElem.GetProperty("value").GetDouble(),
                candElem.GetProperty("delta").GetDouble());
        }

        TimeSeriesThresholds? thresholds = null;
        if (root.TryGetProperty("thresholds", out var threshElem))
        {
            double? min = threshElem.TryGetProperty("min", out var minP) ? minP.GetDouble() : null;
            double? max = threshElem.TryGetProperty("max", out var maxP) ? maxP.GetDouble() : null;
            double? tgt = threshElem.TryGetProperty("target", out var tgtP) ? tgtP.GetDouble() : null;
            thresholds = new TimeSeriesThresholds(min, max, tgt);
        }

        return new TimeSeriesProjection(metricId, subjectId, unit, points, candidate, thresholds);
    }
}

/// <summary>Renders deterministic time-series charts (SVG & ASCII) using Design System tokens.</summary>
public static class TimeSeriesGraph
{
    private static readonly char[] SparklineBlocks = [' ', '▂', '▃', '▄', '▅', '▆', '▇', '█'];

    /// <summary>Renders an ASCII sparkline string (e.g.  ▃▅▇█) for CLI reports.</summary>
    public static string RenderAsciiSparkline(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return string.Empty;
        if (values.Count == 1) return "▄";

        double min = values.Min();
        double max = values.Max();
        double range = max - min;

        var sb = new StringBuilder(values.Count);
        foreach (var val in values)
        {
            if (range <= 0.000001)
            {
                sb.Append('▄');
            }
            else
            {
                int index = (int)Math.Round((val - min) / range * (SparklineBlocks.Length - 1));
                index = Math.Clamp(index, 0, SparklineBlocks.Length - 1);
                sb.Append(SparklineBlocks[index]);
            }
        }
        return sb.ToString();
    }

    /// <summary>Renders an inline SVG sparkline matching @rtkelly13/design-system colors.</summary>
    public static string RenderSvgSparkline(IReadOnlyList<double> values, int width = 120, int height = 28)
    {
        if (values.Count == 0) return string.Empty;

        double min = values.Min();
        double max = values.Max();
        double range = Math.Max(max - min, 0.0001);

        double padding = 3.0;
        double plotW = width - (padding * 2);
        double plotH = height - (padding * 2);

        var points = new StringBuilder();
        for (int i = 0; i < values.Count; i++)
        {
            double x = padding + (values.Count == 1 ? plotW / 2 : (double)i / (values.Count - 1) * plotW);
            double normalizedY = (values[i] - min) / range;
            double y = height - padding - (normalizedY * plotH);

            if (i > 0) points.Append(' ');
            points.Append(x.ToString("F1", CultureInfo.InvariantCulture))
                  .Append(',')
                  .Append(y.ToString("F1", CultureInfo.InvariantCulture));
        }

        return $"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" role=\"img\" aria-label=\"Metric trend\" style=\"overflow:visible;\">" +
               $"<polyline fill=\"none\" stroke=\"var(--ds-intent-info, #0369a1)\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" points=\"{points}\" />" +
               $"</svg>";
    }

    /// <summary>Renders a full historical evolution SVG chart with thresholds and candidate overlay.</summary>
    public static string RenderSvgEvolutionChart(TimeSeriesProjection projection, int width = 640, int height = 220)
    {
        double padLeft = 55.0;
        double padRight = 35.0;
        double padTop = 30.0;
        double padBottom = 40.0;

        double plotW = width - padLeft - padRight;
        double plotH = height - padTop - padBottom;

        var allValues = projection.Points.Select(p => p.Value).ToList();
        if (projection.Candidate != null) allValues.Add(projection.Candidate.Value);
        if (projection.Thresholds?.Min.HasValue == true) allValues.Add(projection.Thresholds.Min.Value);
        if (projection.Thresholds?.Max.HasValue == true) allValues.Add(projection.Thresholds.Max.Value);

        if (allValues.Count == 0) return string.Empty;

        double valMin = allValues.Min();
        double valMax = allValues.Max();
        if (valMax - valMin < 0.001)
        {
            valMin -= 1.0;
            valMax += 1.0;
        }
        else
        {
            double margin = (valMax - valMin) * 0.15;
            valMin -= margin;
            valMax += margin;
        }

        double valRange = valMax - valMin;

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg width=\"100%\" viewBox=\"0 0 {width} {height}\" class=\"diffra-chart\" role=\"img\" aria-label=\"Historical evolution of {projection.MetricId}\" style=\"max-width:{width}px;background:var(--ds-surface-sunken, #eef3f7);border-radius:0.375rem;padding:0.5rem;box-sizing:border-box;font-family:inherit;\">");

        // Title
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{padLeft:F1}\" y=\"18\" fill=\"var(--ds-text-primary, #18212b)\" font-size=\"12\" font-weight=\"700\">{projection.MetricId} ({projection.Unit})</text>");

        // Grid lines (3 horizontal lines)
        for (int i = 0; i <= 3; i++)
        {
            double ratio = (double)i / 3;
            double yVal = valMin + (ratio * valRange);
            double yPos = padTop + plotH - (ratio * plotH);

            sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{padLeft:F1}\" y1=\"{yPos:F1}\" x2=\"{(padLeft + plotW):F1}\" y2=\"{yPos:F1}\" stroke=\"var(--ds-border-default, #d5dde5)\" stroke-dasharray=\"2 4\" stroke-width=\"1\" />");
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{(padLeft - 8):F1}\" y=\"{(yPos + 4):F1}\" fill=\"var(--ds-text-muted, #6b7280)\" font-size=\"10\" text-anchor=\"end\">{yVal:F1}</text>");
        }

        // Threshold line (if specified)
        if (projection.Thresholds?.Min.HasValue == true)
        {
            double thresh = projection.Thresholds.Min.Value;
            double yPos = padTop + plotH - ((thresh - valMin) / valRange * plotH);
            sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{padLeft:F1}\" y1=\"{yPos:F1}\" x2=\"{(padLeft + plotW):F1}\" y2=\"{yPos:F1}\" stroke=\"var(--ds-intent-danger, #b91c1c)\" stroke-dasharray=\"4 4\" stroke-width=\"1.5\" />");
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{(padLeft + plotW):F1}\" y=\"{(yPos - 4):F1}\" fill=\"var(--ds-intent-danger, #b91c1c)\" font-size=\"10\" font-weight=\"700\" text-anchor=\"end\">Min: {thresh:F1}</text>");
        }

        // Plot historical points
        int count = projection.Points.Count + (projection.Candidate != null ? 1 : 0);
        var polylinePoints = new StringBuilder();

        for (int i = 0; i < projection.Points.Count; i++)
        {
            var p = projection.Points[i];
            double x = padLeft + (count == 1 ? plotW / 2 : (double)i / (count - 1) * plotW);
            double y = padTop + plotH - ((p.Value - valMin) / valRange * plotH);

            if (i > 0) polylinePoints.Append(' ');
            polylinePoints.Append(x.ToString("F1", CultureInfo.InvariantCulture)).Append(',').Append(y.ToString("F1", CultureInfo.InvariantCulture));

            // Circle marker
            sb.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{x:F1}\" cy=\"{y:F1}\" r=\"3.5\" fill=\"var(--ds-intent-info, #0369a1)\" />");

            // Label commit short hash
            string label = !string.IsNullOrEmpty(p.Tag) ? p.Tag : (p.Commit.Length >= 7 ? p.Commit[..7] : p.Commit);
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{x:F1}\" y=\"{(height - 15):F1}\" fill=\"var(--ds-text-secondary, #4b5563)\" font-size=\"9\" text-anchor=\"middle\">{label}</text>");
        }

        // Historical line
        sb.Append(CultureInfo.InvariantCulture, $"<polyline fill=\"none\" stroke=\"var(--ds-intent-info, #0369a1)\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" points=\"{polylinePoints}\" />");

        // Candidate overlay
        if (projection.Candidate != null)
        {
            int candIndex = count - 1;
            double candX = padLeft + plotW;
            double candY = padTop + plotH - ((projection.Candidate.Value - valMin) / valRange * plotH);

            // Connect last historical point to candidate with a dashed transition line
            if (projection.Points.Count > 0)
            {
                var lastHist = projection.Points[^1];
                double lastX = padLeft + (count == 1 ? plotW / 2 : (double)(projection.Points.Count - 1) / (count - 1) * plotW);
                double lastY = padTop + plotH - ((lastHist.Value - valMin) / valRange * plotH);
                sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{lastX:F1}\" y1=\"{lastY:F1}\" x2=\"{candX:F1}\" y2=\"{candY:F1}\" stroke=\"var(--ds-text-muted, #6b7280)\" stroke-dasharray=\"3 3\" stroke-width=\"1.5\" />");
            }

            string candFill = projection.Candidate.Delta >= 0 ? "var(--ds-intent-success, #107c41)" : "var(--ds-intent-danger, #b91c1c)";
            sb.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{candX:F1}\" cy=\"{candY:F1}\" r=\"5\" fill=\"{candFill}\" stroke=\"var(--ds-surface-raised, #ffffff)\" stroke-width=\"2\" />");
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{candX:F1}\" y=\"{(height - 15):F1}\" fill=\"var(--ds-text-primary, #18212b)\" font-size=\"9\" font-weight=\"800\" text-anchor=\"middle\">PR</text>");

            // Value badge callout
            string deltaLabel = (projection.Candidate.Delta >= 0 ? "+" : "") + projection.Candidate.Delta.ToString("F1", CultureInfo.InvariantCulture);
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{candX:F1}\" y=\"{(candY - 8):F1}\" fill=\"{candFill}\" font-size=\"10\" font-weight=\"800\" text-anchor=\"middle\">{projection.Candidate.Value:F1} ({deltaLabel})</text>");
        }

        sb.Append("</svg>");
        return sb.ToString();
    }
}
