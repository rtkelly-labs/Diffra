using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Diffra.Protocol;

/// <summary>Canonicalizes the bounded JSON subset supported by Diffra protocol v1.</summary>
public static class CanonicalJson
{
    /// <summary>Maximum accepted JSON input size, in bytes.</summary>
    public const int MaximumDocumentBytes = 4 * 1024 * 1024;

    /// <summary>Maximum accepted number of nested arrays and objects.</summary>
    public const int MaximumDepth = 64;

    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaximumDepth,
    };

    /// <summary>
    /// Parses and canonicalizes one UTF-8 JSON value. Only integer number tokens in the JavaScript
    /// safe-integer range are accepted; object names must be unique.
    /// </summary>
    /// <exception cref="JsonException">The input is invalid or outside the supported v1 subset.</exception>
    public static byte[] Canonicalize(ReadOnlySpan<byte> utf8Json)
    {
        using var document = Parse(utf8Json);
        var output = new ArrayBufferWriter<byte>();
        WriteValue(document.RootElement, output, excludedRootProperty: null);
        return output.WrittenSpan.ToArray();
    }

    internal static byte[] CanonicalizeWithoutRootProperty(ReadOnlySpan<byte> utf8Json, string propertyName)
    {
        using var document = Parse(utf8Json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("An identity projection must be a JSON object.");
        }

        var output = new ArrayBufferWriter<byte>();
        WriteValue(document.RootElement, output, propertyName);
        return output.WrittenSpan.ToArray();
    }

    internal static byte[] CanonicalizeRootProjection(ReadOnlySpan<byte> utf8Json, IReadOnlySet<string> propertyNames)
    {
        using var document = Parse(utf8Json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A document identity projection must be a JSON object.");
        }

        var output = new ArrayBufferWriter<byte>();
        WriteValue(document.RootElement, output, excludedRootProperty: null, includedRootProperties: propertyNames);
        return output.WrittenSpan.ToArray();
    }

    internal static JsonDocument Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaximumDocumentBytes)
        {
            throw new JsonException($"JSON input exceeds the {MaximumDocumentBytes}-byte limit.");
        }

        if (utf8Json.Length >= 3 && utf8Json[0] == 0xEF && utf8Json[1] == 0xBB && utf8Json[2] == 0xBF)
        {
            throw new JsonException("JSON input must not contain a UTF-8 byte-order mark.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json.ToArray(), DocumentOptions);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("JSON input is not valid UTF-8.", exception);
        }

        try
        {
            ValidateValue(document.RootElement);
            return document;
        }
        catch (DecoderFallbackException exception)
        {
            document.Dispose();
            throw new JsonException("JSON input contains invalid UTF-8.", exception);
        }
        catch (InvalidOperationException exception)
        {
            document.Dispose();
            throw new JsonException("JSON input contains invalid UTF-8 or an invalid Unicode scalar value.", exception);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static void ValidateValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    var name = property.Name;
                    ValidateUnicode(name);
                    if (!names.Add(name))
                    {
                        throw new JsonException($"Duplicate JSON object member: {name}");
                    }

                    ValidateValue(property.Value);
                }

                break;
            }
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    ValidateValue(item);
                }

                break;
            case JsonValueKind.String:
                ValidateUnicode(element.GetString()!);
                break;
            case JsonValueKind.Number:
                _ = ParseSafeInteger(element.GetRawText());
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                break;
            default:
                throw new JsonException("Unsupported JSON value.");
        }
    }

    private static long ParseSafeInteger(string token)
    {
        if (!long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            || value < -MaximumSafeInteger
            || value > MaximumSafeInteger)
        {
            throw new JsonException("JSON numbers must be integer tokens within the JavaScript safe-integer range.");
        }

        return value;
    }

    private static void WriteValue(
        JsonElement element,
        IBufferWriter<byte> output,
        string? excludedRootProperty,
        IReadOnlySet<string>? includedRootProperties = null)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                WriteAscii(output, "{");
                var properties = element.EnumerateObject()
                    .Where(property => excludedRootProperty is null
                        || !string.Equals(property.Name, excludedRootProperty, StringComparison.Ordinal))
                    .Where(property => includedRootProperties is null || includedRootProperties.Contains(property.Name))
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .ToArray();
                for (var index = 0; index < properties.Length; index++)
                {
                    if (index != 0)
                    {
                        WriteAscii(output, ",");
                    }

                    WriteString(output, properties[index].Name);
                    WriteAscii(output, ":");
                    WriteValue(properties[index].Value, output, excludedRootProperty: null);
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
                throw new JsonException("Unsupported JSON value.");
        }
    }

    private static void WriteString(IBufferWriter<byte> output, string value)
    {
        WriteAscii(output, "\"");
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            switch (character)
            {
                case '"': WriteAscii(output, "\\\""); break;
                case '\\': WriteAscii(output, "\\\\"); break;
                case '\b': WriteAscii(output, "\\b"); break;
                case '\t': WriteAscii(output, "\\t"); break;
                case '\n': WriteAscii(output, "\\n"); break;
                case '\f': WriteAscii(output, "\\f"); break;
                case '\r': WriteAscii(output, "\\r"); break;
                default:
                    if (character < 0x20)
                    {
                        WriteAscii(output, "\\u00");
                        WriteByte(output, (byte)"0123456789abcdef"[character >> 4]);
                        WriteByte(output, (byte)"0123456789abcdef"[character & 0x0F]);
                    }
                    else if (char.IsHighSurrogate(character))
                    {
                        var scalar = char.ConvertToUtf32(character, value[++index]);
                        WriteRune(output, new Rune(scalar));
                    }
                    else if (char.IsLowSurrogate(character))
                    {
                        throw new JsonException("JSON strings must contain valid Unicode scalar values.");
                    }
                    else
                    {
                        WriteRune(output, new Rune(character));
                    }

                    break;
            }
        }

        WriteAscii(output, "\"");
    }

    private static void ValidateUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    throw new JsonException("JSON strings must contain valid Unicode scalar values.");
                }

                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                throw new JsonException("JSON strings must contain valid Unicode scalar values.");
            }
        }
    }

    private static void WriteRune(IBufferWriter<byte> output, Rune rune)
    {
        var span = output.GetSpan(rune.Utf8SequenceLength);
        var written = rune.EncodeToUtf8(span);
        output.Advance(written);
    }

    private static void WriteAscii(IBufferWriter<byte> output, string value)
    {
        var byteCount = Encoding.ASCII.GetByteCount(value);
        var span = output.GetSpan(byteCount);
        var written = Encoding.ASCII.GetBytes(value, span);
        output.Advance(written);
    }

    private static void WriteByte(IBufferWriter<byte> output, byte value)
    {
        output.GetSpan(1)[0] = value;
        output.Advance(1);
    }
}
