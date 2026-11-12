using System.Globalization;
using System.Text;

namespace Diffra.Protocol.Properties;

/// <summary>Builds record entries as the JSON lines a job would write.</summary>
internal static class Entries
{
    public const string Commit = "0123456789abcdef0123456789abcdef01234567";
    public const string OtherCommit = "fedcba9876543210fedcba9876543210fedcba98";

    private const string Source =
        "[{\"path\":\"out/data.json\",\"format\":\"json\",\"sha256\":\"2d711642b726b04401627ca9fbac32f5c8530fb1903cc4db02258717921a4881\"}]";

    /// <summary>A recorded entry whose data was read from files.</summary>
    public static string FromData(string job, int attempt, string dataset, IReadOnlyList<(string Name, string Value)> dimensions, string data, string commit = Commit, bool replaces = false, string project = "main") =>
        Head(job, attempt, dataset, dimensions, commit, project)
            .Append(replaces ? ",\"status\":\"recorded\",\"replaces\":true,\"sources\":" : ",\"status\":\"recorded\",\"sources\":").Append(Source)
            .Append(",\"data\":").Append(data).Append('}').ToString();

    /// <summary>A recorded entry given with <c>--value</c>.</summary>
    public static string FromValue(
        string job,
        int attempt,
        string dataset,
        IReadOnlyList<(string Name, string Value)> dimensions,
        string? key,
        string value,
        string? unit,
        bool replaces,
        string commit = Commit,
        string project = "main")
    {
        var builder = Head(job, attempt, dataset, dimensions, commit, project);
        if (key is not null)
        {
            builder.Append(",\"key\":").Append(Quote(key));
        }

        builder.Append(",\"status\":\"recorded\"");
        if (replaces)
        {
            builder.Append(",\"replaces\":true");
        }

        builder.Append(",\"value\":{\"value\":").Append(Quote(value));
        if (unit is not null)
        {
            builder.Append(",\"unit\":").Append(Quote(unit));
        }

        return builder.Append("}}").ToString();
    }

    /// <summary>A skipped or failed entry.</summary>
    public static string Unrecorded(string job, int attempt, string dataset, IReadOnlyList<(string Name, string Value)> dimensions, bool failed, string reason, string commit = Commit, string project = "main") =>
        Head(job, attempt, dataset, dimensions, commit, project)
            .Append(",\"status\":").Append(failed ? "\"failed\"" : "\"skipped\"")
            .Append(",\"reason\":").Append(Quote(reason)).Append('}').ToString();

    /// <summary>A rejected entry, which keeps what was given and why it was refused.</summary>
    public static string Rejected(string job, int attempt, string dataset, IReadOnlyList<(string Name, string Value)> dimensions, string given, string commit = Commit, string project = "main") =>
        Head(job, attempt, dataset, dimensions, commit, project)
            .Append(",\"status\":\"rejected\",\"input\":{\"name\":").Append(Quote(dataset))
            .Append(",\"value\":").Append(Quote(given))
            .Append("},\"problems\":[").Append(Quote($"'{given}' is not a number.")).Append("]}").ToString();

    private static StringBuilder Head(string job, int attempt, string dataset, IReadOnlyList<(string Name, string Value)> dimensions, string commit, string project)
    {
        var builder = new StringBuilder();
        builder.Append("{\"schema\":").Append(Quote(RecordEntries.SchemaId))
            .Append(",\"project\":").Append(Quote(project)).Append(",\"dataset\":").Append(Quote(dataset))
            .Append(",\"commit\":").Append(Quote(commit))
            .Append(",\"job\":{\"name\":").Append(Quote(job))
            .Append(",\"attempt\":").Append(attempt.ToString(CultureInfo.InvariantCulture)).Append('}');
        if (dimensions.Count > 0)
        {
            builder.Append(",\"dimensions\":{")
                .AppendJoin(',', dimensions.Select(dimension => $"{Quote(dimension.Name)}:{Quote(dimension.Value)}"))
                .Append('}');
        }

        return builder;
    }

    private static string Quote(string text) => Json.Render(new JStr(text));
}
