using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Diffra.LineCounts.Oracle;

/// <summary>
/// Reads and writes <c>tests/line-counts/oracle.json</c>: the pinned tools and, per case, its
/// digest, its physical line count and each tool's counts. Keys are written in ordinal order, so
/// recording twice with the same tools gives the same bytes.
/// </summary>
internal static class OracleJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NewLine = "\n",
    };

    public static string Write(Oracle oracle)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("tools");
            foreach (var tool in Tools.All)
            {
                writer.WriteStartObject(tool.Name);
                writer.WriteString("version", oracle.Versions[tool.Name]);
                writer.WriteString("release", tool.Release);
                writer.WriteStartArray("arguments");
                foreach (var argument in tool.Arguments)
                {
                    writer.WriteStringValue(argument);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteStartObject("cases");
            foreach (var result in oracle.Cases.OrderBy(c => c.Path, StringComparer.Ordinal))
            {
                writer.WriteStartObject(result.Path);
                writer.WriteString("sha256", result.Sha256);
                writer.WriteNumber("lines", result.Lines);
                foreach (var (name, counts) in result.Results)
                {
                    writer.WriteStartObject(name);
                    if (counts.Counted)
                    {
                        writer.WriteString("language", counts.Language);
                        writer.WriteNumber("code", counts.Code);
                        writer.WriteNumber("comments", counts.Comments);
                        writer.WriteNumber("blanks", counts.Blanks);
                    }
                    else
                    {
                        writer.WriteBoolean("counted", false);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    public static Oracle Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tool in document.RootElement.GetProperty("tools").EnumerateObject())
        {
            versions[tool.Name] = tool.Value.GetProperty("version").GetString() ?? string.Empty;
        }

        var cases = new List<CaseResult>();
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateObject())
        {
            var results = new SortedDictionary<string, ToolResult>(StringComparer.Ordinal);
            foreach (var property in item.Value.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                results[property.Name] = property.Value.TryGetProperty("counted", out _)
                    ? ToolResult.NotCounted
                    : new ToolResult(
                        true,
                        property.Value.GetProperty("language").GetString(),
                        property.Value.GetProperty("code").GetInt64(),
                        property.Value.GetProperty("comments").GetInt64(),
                        property.Value.GetProperty("blanks").GetInt64());
            }

            cases.Add(new CaseResult(item.Name, item.Value.GetProperty("sha256").GetString() ?? string.Empty, item.Value.GetProperty("lines").GetInt32(), results));
        }

        return new Oracle(versions, cases);
    }
}
