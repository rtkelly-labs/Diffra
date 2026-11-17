using System.Text.Json;

namespace Diffra.LineCounts.Oracle;

/// <summary>
/// What Diffra's counter is expected to give for one case (<c>tests/line-counts/rulings.json</c>).
/// Every case says what it isolates and has one of three rulings:
/// <list type="bullet">
/// <item><c>majority</c>: agree with the triple at least two tools give (or, when at least two
/// tools do not count the file, do not count it either).</item>
/// <item><c>diverge</c>: a deliberate difference, with the expected triple and the reason.</item>
/// <item><c>open</c>: not yet decided, with the question for the owner. No case may stay open once
/// the counter is built.</item>
/// </list>
/// </summary>
internal sealed record Ruling(string Kind, string Isolates, Counts? Expected, string? Reason)
{
    public IEnumerable<string> Problems(string path, CaseResult result)
    {
        if (string.IsNullOrWhiteSpace(Isolates))
        {
            yield return $"{path}: the ruling does not say what the case isolates";
        }

        switch (Kind)
        {
            case "majority":
                if (result.Majority() is null && !result.MajorityNotCounted)
                {
                    yield return $"{path}: ruled 'majority', but the tools split three ways";
                }

                if (Expected is not null || Reason is not null)
                {
                    yield return $"{path}: a 'majority' ruling takes no expected counts or reason";
                }

                break;
            case "diverge":
                if (Expected is null || string.IsNullOrWhiteSpace(Reason))
                {
                    yield return $"{path}: a 'diverge' ruling needs expected counts and a reason";
                }
                else if (Expected == result.Majority())
                {
                    yield return $"{path}: ruled 'diverge', but the expected counts are the majority's";
                }

                break;
            case "open":
                if (string.IsNullOrWhiteSpace(Reason))
                {
                    yield return $"{path}: an 'open' ruling needs the question in 'reason'";
                }

                break;
            default:
                yield return $"{path}: unknown ruling '{Kind}'";
                break;
        }
    }

    /// <summary>The counts Diffra's counter must give, or <c>null</c> when it must not count the file or the case is open.</summary>
    public Counts? ExpectedFor(CaseResult result) => Kind switch
    {
        "majority" => result.Majority(),
        "diverge" => Expected,
        _ => null,
    };
}

internal static class Rulings
{
    public static SortedDictionary<string, Ruling> Read(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow });
        var rulings = new SortedDictionary<string, Ruling>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateObject())
        {
            var value = item.Value;
            Counts? expected = null;
            if (value.TryGetProperty("expected", out var counts))
            {
                expected = new Counts(counts.GetProperty("code").GetInt64(), counts.GetProperty("comments").GetInt64(), counts.GetProperty("blanks").GetInt64());
            }

            rulings[item.Name] = new Ruling(
                value.GetProperty("ruling").GetString() ?? string.Empty,
                value.TryGetProperty("isolates", out var isolates) ? isolates.GetString() ?? string.Empty : string.Empty,
                expected,
                value.TryGetProperty("reason", out var reason) ? reason.GetString() : null);
        }

        return rulings;
    }
}
