using System.Text.Json;
using System.Text.Json.Nodes;

namespace Diffra.Protocol;

public sealed record MethodComplexity(
    string MethodId,
    int Complexity,
    int LinesOfCode);

public sealed record ComplexityQuantiles(
    double P50,
    double P75,
    double P90,
    double P99);

public sealed record ComplexitySummary(
    int TotalMethods,
    int TotalComplexity,
    double MeanComplexity,
    ComplexityQuantiles Quantiles,
    IReadOnlyList<MethodComplexity> Outliers);

public sealed record ComplexityPayload(
    string Scope,
    string Methodology,
    string Unit,
    ComplexitySummary Summary,
    IReadOnlyList<MethodComplexity> Methods)
{
    public const string SchemaUri = "https://diffra.dev/schemas/complexity-v1.schema.json";
    public const string UnitName = "cyclomatic-complexity";

    public JsonObject ToJsonObject()
    {
        var methodsArray = new JsonArray();
        foreach (var m in Methods.OrderBy(m => m.MethodId, StringComparer.Ordinal))
        {
            methodsArray.Add(new JsonObject
            {
                ["methodId"] = m.MethodId,
                ["complexity"] = m.Complexity,
                ["linesOfCode"] = m.LinesOfCode,
            });
        }

        var outliersArray = new JsonArray();
        foreach (var m in Summary.Outliers.OrderBy(m => m.MethodId, StringComparer.Ordinal))
        {
            outliersArray.Add(new JsonObject
            {
                ["methodId"] = m.MethodId,
                ["complexity"] = m.Complexity,
                ["linesOfCode"] = m.LinesOfCode,
            });
        }

        return new JsonObject
        {
            ["schema"] = SchemaUri,
            ["scope"] = Scope,
            ["methodology"] = Methodology,
            ["unit"] = UnitName,
            ["summary"] = new JsonObject
            {
                ["totalMethods"] = Summary.TotalMethods,
                ["totalComplexity"] = Summary.TotalComplexity,
                ["meanComplexity"] = Summary.MeanComplexity,
                ["quantiles"] = new JsonObject
                {
                    ["p50"] = Summary.Quantiles.P50,
                    ["p75"] = Summary.Quantiles.P75,
                    ["p90"] = Summary.Quantiles.P90,
                    ["p99"] = Summary.Quantiles.P99,
                },
                ["outliers"] = outliersArray,
            },
            ["methods"] = methodsArray,
        };
    }

    public static ComplexityPayload FromJsonElement(JsonElement root)
    {
        var scope = root.GetProperty("scope").GetString()!;
        var methodology = root.GetProperty("methodology").GetString()!;
        var unit = root.GetProperty("unit").GetString()!;

        var summaryElem = root.GetProperty("summary");
        var totalMethods = summaryElem.GetProperty("totalMethods").GetInt32();
        var totalComplexity = summaryElem.GetProperty("totalComplexity").GetInt32();
        var meanComplexity = summaryElem.GetProperty("meanComplexity").GetDouble();

        var quantilesElem = summaryElem.GetProperty("quantiles");
        var quantiles = new ComplexityQuantiles(
            quantilesElem.GetProperty("p50").GetDouble(),
            quantilesElem.GetProperty("p75").GetDouble(),
            quantilesElem.GetProperty("p90").GetDouble(),
            quantilesElem.GetProperty("p99").GetDouble());

        var outliers = new List<MethodComplexity>();
        foreach (var item in summaryElem.GetProperty("outliers").EnumerateArray())
        {
            outliers.Add(new MethodComplexity(
                item.GetProperty("methodId").GetString()!,
                item.GetProperty("complexity").GetInt32(),
                item.GetProperty("linesOfCode").GetInt32()));
        }

        var methods = new List<MethodComplexity>();
        foreach (var item in root.GetProperty("methods").EnumerateArray())
        {
            methods.Add(new MethodComplexity(
                item.GetProperty("methodId").GetString()!,
                item.GetProperty("complexity").GetInt32(),
                item.GetProperty("linesOfCode").GetInt32()));
        }

        var summary = new ComplexitySummary(totalMethods, totalComplexity, meanComplexity, quantiles, outliers);
        return new ComplexityPayload(scope, methodology, unit, summary, methods);
    }

    public static ComplexityPayload Compute(
        string scope,
        string methodology,
        IReadOnlyList<MethodComplexity> methods,
        int outlierThreshold = 15)
    {
        ArgumentNullException.ThrowIfNull(methods);
        int totalMethods = methods.Count;
        int totalComplexity = methods.Sum(m => m.Complexity);
        double mean = totalMethods == 0 ? 0.0 : Math.Round((double)totalComplexity / totalMethods, 2);

        var sortedValues = methods.Select(m => (double)m.Complexity).OrderBy(c => c).ToList();
        var quantiles = new ComplexityQuantiles(
            P50: GetQuantile(sortedValues, 0.50),
            P75: GetQuantile(sortedValues, 0.75),
            P90: GetQuantile(sortedValues, 0.90),
            P99: GetQuantile(sortedValues, 0.99));

        var outliers = methods.Where(m => m.Complexity >= outlierThreshold).OrderByDescending(m => m.Complexity).ToList();
        var summary = new ComplexitySummary(totalMethods, totalComplexity, mean, quantiles, outliers);

        return new ComplexityPayload(scope, methodology, UnitName, summary, methods);
    }

    private static double GetQuantile(List<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0.0;
        if (sorted.Count == 1) return sorted[0];

        double position = (sorted.Count - 1) * percentile;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];

        double fraction = position - lower;
        return Math.Round(sorted[lower] + (fraction * (sorted[upper] - sorted[lower])), 2);
    }
}

public sealed record ComplexityDelta(
    double MeanComplexityDelta,
    int TotalMethodsDelta,
    IReadOnlyList<MethodComplexity> AddedMethods,
    IReadOnlyList<MethodComplexity> RemovedMethods,
    IReadOnlyList<MethodComplexityShift> ModifiedMethods,
    IReadOnlyList<MethodComplexity> NewOutliers);

public sealed record MethodComplexityShift(
    string MethodId,
    int BeforeComplexity,
    int AfterComplexity,
    int Delta);

public sealed record ComplexityPolicy(
    double MaxMeanComplexity,
    int OutlierThreshold,
    bool FailOnOutlierGrowth);

public static class ComplexityEvaluator
{
    public static ComplexityDelta Compare(ComplexityPayload baseline, ComplexityPayload candidate, int outlierThreshold = 15)
    {
        var meanDelta = Math.Round(candidate.Summary.MeanComplexity - baseline.Summary.MeanComplexity, 2);
        var totalMethodsDelta = candidate.Summary.TotalMethods - baseline.Summary.TotalMethods;

        var baseMap = baseline.Methods.ToDictionary(m => m.MethodId, m => m, StringComparer.Ordinal);
        var candMap = candidate.Methods.ToDictionary(m => m.MethodId, m => m, StringComparer.Ordinal);

        var added = new List<MethodComplexity>();
        var removed = new List<MethodComplexity>();
        var modified = new List<MethodComplexityShift>();
        var newOutliers = new List<MethodComplexity>();

        foreach (var (id, candMethod) in candMap)
        {
            if (baseMap.TryGetValue(id, out var baseMethod))
            {
                if (candMethod.Complexity != baseMethod.Complexity)
                {
                    modified.Add(new MethodComplexityShift(id, baseMethod.Complexity, candMethod.Complexity, candMethod.Complexity - baseMethod.Complexity));
                }

                if (candMethod.Complexity >= outlierThreshold && baseMethod.Complexity < outlierThreshold)
                {
                    newOutliers.Add(candMethod);
                }
            }
            else
            {
                added.Add(candMethod);
                if (candMethod.Complexity >= outlierThreshold)
                {
                    newOutliers.Add(candMethod);
                }
            }
        }

        foreach (var (id, baseMethod) in baseMap)
        {
            if (!candMap.ContainsKey(id))
            {
                removed.Add(baseMethod);
            }
        }

        return new ComplexityDelta(meanDelta, totalMethodsDelta, added, removed, modified, newOutliers);
    }

    public static (string Outcome, IReadOnlyList<AssessmentFinding> Findings) Evaluate(
        ComplexityPayload candidate,
        ComplexityDelta delta,
        ComplexityPolicy policy)
    {
        var findings = new List<AssessmentFinding>();
        var outcome = "pass";

        // 1. Mean complexity check
        if (candidate.Summary.MeanComplexity > policy.MaxMeanComplexity)
        {
            outcome = "fail";
            findings.Add(new AssessmentFinding(
                "complexity/mean-threshold",
                "fail",
                $"Mean complexity {candidate.Summary.MeanComplexity:F2} exceeds permitted maximum of {policy.MaxMeanComplexity:F2}.",
                ["summary.meanComplexity"],
                candidate.Summary.MeanComplexity,
                policy.MaxMeanComplexity));
        }

        // 2. Outlier growth
        if (delta.NewOutliers.Count > 0)
        {
            var severity = policy.FailOnOutlierGrowth ? "fail" : "warn";
            if (severity == "fail") outcome = "fail";
            else if (outcome == "pass") outcome = "warn";

            foreach (var outlier in delta.NewOutliers)
            {
                findings.Add(new AssessmentFinding(
                    "complexity/new-outlier",
                    severity,
                    $"New complexity outlier detected in '{outlier.MethodId}' with score {outlier.Complexity} (threshold: {policy.OutlierThreshold}).",
                    [outlier.MethodId],
                    outlier.Complexity,
                    policy.OutlierThreshold));
            }
        }

        if (findings.Count == 0)
        {
            findings.Add(new AssessmentFinding(
                "complexity/policy",
                "pass",
                $"Complexity is within policy bounds (Mean: {candidate.Summary.MeanComplexity:F2}, Outliers: {candidate.Summary.Outliers.Count}).",
                [],
                candidate.Summary.MeanComplexity,
                null));
        }

        return (outcome, findings);
    }
}
