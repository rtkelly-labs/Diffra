using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Diffra.Regression;

public static class CanonicalHashHelper
{
    private const long MaximumSafeInteger = 9_007_199_254_740_991;

    public static string ComputeDocumentId(string kind, ReadOnlySpan<byte> documentJson)
    {
        var projection = Canonicalize(documentJson, excludedRootProperty: "id");
        ReadOnlySpan<byte> domain = kind switch
        {
            "delta" => "diffra/v1\0delta\0"u8,
            "evidence" => "diffra/v1\0evidence\0"u8,
            "baseline-reference" => "diffra/v1\0baseline-reference\0"u8,
            "assessment" => "diffra/v1\0assessment\0"u8,
            _ => Encoding.UTF8.GetBytes($"diffra/v1\0{kind}\0")
        };
        var input = new byte[domain.Length + projection.Length];
        domain.CopyTo(input);
        projection.CopyTo(input.AsSpan(domain.Length));
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(input))}";
    }

    public static string ComputeEvidenceId(ReadOnlySpan<byte> evidenceJson)
    {
        return ComputeDocumentId("evidence", evidenceJson);
    }

    private static byte[] Canonicalize(ReadOnlySpan<byte> utf8Json, string? excludedRootProperty = null)
    {
        using var doc = JsonDocument.Parse(utf8Json.ToArray());
        var output = new ArrayBufferWriter<byte>();
        WriteValue(doc.RootElement, output, excludedRootProperty);
        return output.WrittenSpan.ToArray();
    }

    private static void WriteValue(
        JsonElement element,
        IBufferWriter<byte> output,
        string? excludedRootProperty)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    WriteAscii(output, "{");
                    var properties = element.EnumerateObject()
                        .Where(p => excludedRootProperty is null || !string.Equals(p.Name, excludedRootProperty, StringComparison.Ordinal))
                        .OrderBy(p => p.Name, StringComparer.Ordinal)
                        .ToArray();
                    for (var i = 0; i < properties.Length; i++)
                    {
                        if (i != 0)
                        {
                            WriteAscii(output, ",");
                        }
                        WriteString(output, properties[i].Name);
                        WriteAscii(output, ":");
                        WriteValue(properties[i].Value, output, excludedRootProperty: null);
                    }
                    WriteAscii(output, "}");
                    break;
                }
            case JsonValueKind.Array:
                {
                    WriteAscii(output, "[");
                    var first = true;
                    foreach (var item in element.EnumerateArray())
                    {
                        if (!first)
                        {
                            WriteAscii(output, ",");
                        }
                        WriteValue(item, output, excludedRootProperty: null);
                        first = false;
                    }
                    WriteAscii(output, "]");
                    break;
                }
            case JsonValueKind.String:
                WriteString(output, element.GetString()!);
                break;
            case JsonValueKind.Number:
                WriteAscii(output, ParseSafeInteger(element.GetRawText()).ToString(CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True:
                WriteAscii(output, "true");
                break;
            case JsonValueKind.False:
                WriteAscii(output, "false");
                break;
            case JsonValueKind.Null:
                WriteAscii(output, "null");
                break;
            default:
                throw new JsonException($"Unsupported JSON token kind: {element.ValueKind}");
        }
    }

    private static long ParseSafeInteger(string token)
    {
        if (!long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            || value < -MaximumSafeInteger
            || value > MaximumSafeInteger)
        {
            throw new JsonException("JSON numbers must be integer tokens within JavaScript safe integer range.");
        }
        return value;
    }

    private static void WriteAscii(IBufferWriter<byte> output, string text)
    {
        var span = output.GetSpan(text.Length);
        var bytes = Encoding.ASCII.GetBytes(text, span);
        output.Advance(bytes);
    }

    private static void WriteString(IBufferWriter<byte> output, string value)
    {
        var encoded = JsonEncodedText.Encode(value, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping);
        WriteAscii(output, "\"");
        var span = output.GetSpan(encoded.EncodedUtf8Bytes.Length);
        encoded.EncodedUtf8Bytes.CopyTo(span);
        output.Advance(encoded.EncodedUtf8Bytes.Length);
        WriteAscii(output, "\"");
    }
}
