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
        return setting switch
        {
            FailOn.Never => false,
            FailOn.Fail => findings.Any(f => f.Status == "fail"),
            FailOn.Warn => findings.Any(f => f.Status is "fail" or "warn" or "needs-review" or "inconclusive"),
            _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, "Unknown fail-on value."),
        };
    }
}
