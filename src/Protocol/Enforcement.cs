namespace Diffra.Protocol;

/// <summary>
/// The lowest finding status that fails Diffra's step. Diffra surfaces findings and does not fail a
/// build unless the adopter turns this on with an input of Diffra's CI step.
/// </summary>
public enum FailOn
{
    /// <summary>The step never fails because of a finding. The default.</summary>
    Never,

    /// <summary>The step fails when any finding has the status <c>fail</c>.</summary>
    Fail,

    /// <summary>
    /// The step fails when any finding has the status <c>fail</c> or <c>warn</c>, or a status that
    /// ranks above <c>warn</c>: <c>needs-review</c> or <c>inconclusive</c>.
    /// </summary>
    Warn,
}

/// <summary>Reads the <c>fail-on</c> setting and decides whether a result fails the step.</summary>
public static class Enforcement
{
    /// <summary>The name of the input of Diffra's CI step.</summary>
    public const string SettingName = "fail-on";

    /// <summary>The value used when the adopter sets nothing.</summary>
    public const FailOn Default = FailOn.Never;

    /// <summary>The setting's value as it is written: <c>never</c>, <c>fail</c> or <c>warn</c>.</summary>
    public static string Token(FailOn value) => value switch
    {
        FailOn.Never => "never",
        FailOn.Fail => "fail",
        FailOn.Warn => "warn",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown fail-on value."),
    };

    /// <summary>
    /// Reads a written value. An absent or empty value is the default. Anything else that is not
    /// one of the three values returns false; the caller must treat that as an error rather than
    /// use <paramref name="value"/>, so a misspelt setting never silently turns enforcement off.
    /// </summary>
    public static bool TryParse(string? text, out FailOn value)
    {
        switch (text)
        {
            case null or "":
                value = Default;
                return true;
            case "never":
                value = FailOn.Never;
                return true;
            case "fail":
                value = FailOn.Fail;
                return true;
            case "warn":
                value = FailOn.Warn;
                return true;
            default:
                value = Default;
                return false;
        }
    }

    /// <summary>
    /// Whether the findings fail the step under the setting. <c>fail</c> trips on the status
    /// <c>fail</c>. <c>warn</c> also trips on <c>warn</c>, <c>needs-review</c> and
    /// <c>inconclusive</c>, which the assessment ranks above <c>warn</c>. <c>changed</c> (a change no
    /// rule judged) and <c>pass</c> never fail the step.
    /// </summary>
    public static bool FailsStep(FailOn setting, IEnumerable<RankedFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return findings.Any(finding => Trips(setting, finding.Status));
    }

    /// <summary>
    /// The status a merge finding counts with under <c>fail-on</c>, or null when it does not count.
    /// A required dataset that was not collected, including one collected only as rejected
    /// entries, counts as <c>warn</c>. One whose collector reported that it failed counts as
    /// <c>fail</c>, because the collection broke. Other merge findings do not count yet.
    /// </summary>
    public static string? Status(MergeFindingKind kind) => kind switch
    {
        MergeFindingKind.NotCollected => "warn",
        MergeFindingKind.CollectionFailed => "fail",
        _ => null,
    };

    /// <summary>Whether the merge findings fail the step under the setting.</summary>
    public static bool MergeFailsStep(FailOn setting, IEnumerable<MergeFinding> findings) =>
        Counted(setting, findings).Any();

    /// <summary>The merge findings that count towards failing the step under the setting.</summary>
    public static IEnumerable<MergeFinding> Counted(FailOn setting, IEnumerable<MergeFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return findings.Where(finding => Status(finding.Kind) is { } status && Trips(setting, status));
    }

    private static bool Trips(FailOn setting, string status) => setting switch
    {
        FailOn.Never => false,
        FailOn.Fail => status == "fail",
        FailOn.Warn => status is "fail" or "warn" or "needs-review" or "inconclusive",
        _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, "Unknown fail-on value."),
    };
}
