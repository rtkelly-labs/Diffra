using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Diffra.Converters;

/// <summary>
/// Turns a SARIF artifact location into a path as an entry records it (docs/datasets.md, "The
/// shape of <c>data</c>"): relative to the repository root with forward slashes and no <c>.</c> or
/// <c>..</c> segment. An absolute path inside the repository loses the root; one outside it, or a
/// URI that is not a file, is kept as <c>&lt;outside&gt;/&lt;digest&gt;/&lt;file name&gt;</c>. It
/// works on the text alone, the same way on every platform, and never opens what it names.
/// </summary>
internal static partial class SarifPath
{
    // The first segment of a path outside the repository.
    private const string Outside = "<outside>";

    // How many uriBaseId links are followed, so that a cycle ends.
    private const int MaximumBases = 10;

    /// <summary>
    /// The recorded path of <paramref name="uri"/>, resolved against <paramref name="uriBaseId"/>
    /// in <paramref name="bases"/>, the run's <c>originalUriBaseIds</c> by name. A relative URI
    /// whose base the run does not define is relative to the repository root.
    /// </summary>
    public static string Resolve(string uri, string? uriBaseId, IReadOnlyDictionary<string, JsonElement> bases, string root)
    {
        if (uri.Contains('\\', StringComparison.Ordinal))
        {
            throw new FormatException($"the URI '{Shown(uri)}' has a backslash; a SARIF URI separates folders with '/'.");
        }

        var written = uri;
        var baseId = uriBaseId;
        for (var step = 0; step < MaximumBases && baseId is not null && !HasScheme(written) && !written.StartsWith('/') && !IsDrive(written); step++)
        {
            if (!bases.TryGetValue(baseId, out var entry)
                || entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("uri", out var baseUri)
                || baseUri.ValueKind != JsonValueKind.String)
            {
                break;
            }

            var prefix = baseUri.GetString()!;
            if (prefix.Contains('\\', StringComparison.Ordinal))
            {
                throw new FormatException($"the base URI '{Shown(prefix)}' has a backslash; a SARIF URI separates folders with '/'.");
            }

            written = (prefix.EndsWith('/') || prefix.Length == 0 ? prefix : prefix + "/") + written;
            baseId = entry.TryGetProperty("uriBaseId", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }

        var path = StripQuery(written);
        if (HasScheme(path))
        {
            if (!path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                return OutsideKey(written, LastDecoded(path));
            }

            path = path[5..];
            if (path.StartsWith("//", StringComparison.Ordinal))
            {
                var end = path.IndexOf('/', 2);
                var authority = end < 0 ? path[2..] : path[2..end];
                if (authority.Length > 0 && !authority.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                {
                    return OutsideKey(written, LastDecoded(path));
                }

                path = end < 0 ? "/" : path[end..];
            }

            return Absolute(written, Decode(Segments(path)), root);
        }

        // A path written without a scheme is absolute when it starts with '/' or a drive such as C:/.
        var segments = Decode(Segments(path));
        if (path.StartsWith('/') || IsDrive(path))
        {
            return Absolute(written, segments, root);
        }

        var relative = Normalize(segments);
        return relative is null ? OutsideKey(written, segments.LastOrDefault()) : Relative(relative);
    }

    // An absolute path: relative to the root when it lies under it, and outside otherwise.
    private static string Absolute(string written, List<string> segments, string root)
    {
        // A Windows drive, as in file:///C:/src/a.cs, is the first segment.
        var path = Normalize(segments);
        if (path is null)
        {
            return OutsideKey(written, segments.LastOrDefault());
        }

        var rootSegments = root.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (path.Count > rootSegments.Length && rootSegments.Select((segment, index) => string.Equals(segment, path[index], comparison)).All(same => same))
        {
            return Relative(path.Skip(rootSegments.Length).ToList());
        }

        return OutsideKey(written, path.LastOrDefault());
    }

    // Removes '.' and empty segments and applies '..'; null when a '..' would leave the top.
    private static List<string>? Normalize(List<string> segments)
    {
        var result = new List<string>(segments.Count);
        foreach (var segment in segments)
        {
            if (segment is "" or ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (result.Count == 0)
                {
                    return null;
                }

                result.RemoveAt(result.Count - 1);
                continue;
            }

            result.Add(segment);
        }

        return result;
    }

    private static string Relative(List<string> segments)
    {
        var path = string.Join('/', segments);
        if (path.Length == 0)
        {
            throw new FormatException("the location's URI names the repository root, not a file.");
        }

        return SourceFiles.IsSafePath(path)
            ? path
            : throw new FormatException($"the path '{Shown(path)}' cannot be recorded: a path must be at most 1000 characters of valid Unicode, with no control character and not starting with '~'.");
    }

    // <outside>/<the first 12 hexadecimal characters of the SHA-256 of the URI with its bases
    // applied, query and fragment included>/<file name>. The file name is already decoded.
    private static string OutsideKey(string written, string? file)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SourceFiles.Display(written))))[..12];
        if (string.IsNullOrEmpty(file) || file is "." or "..")
        {
            return $"{Outside}/{digest}";
        }

        return SourceFiles.IsSafePath(file)
            ? $"{Outside}/{digest}/{file}"
            : throw new FormatException($"the file name '{Shown(file)}' cannot be recorded: it must be valid Unicode with no control character.");
    }

    private static List<string> Segments(string path) => [.. path.Split('/')];

    // The last segment of a path, percent-decoded.
    private static string LastDecoded(string path) => Decode([path[(path.LastIndexOf('/') + 1)..]])[0];

    // Percent-decodes each segment. A segment that decodes to a '/' or '\' would change the path's
    // folders, so it is refused.
    private static List<string> Decode(List<string> segments)
    {
        var decoded = new List<string>(segments.Count);
        foreach (var segment in segments)
        {
            var text = Uri.UnescapeDataString(segment);
            if (text.Contains('/', StringComparison.Ordinal) || text.Contains('\\', StringComparison.Ordinal))
            {
                throw new FormatException($"the URI segment '{Shown(segment)}' decodes to a folder separator.");
            }

            decoded.Add(text);
        }

        return decoded;
    }

    // A URI's path without its query or fragment.
    private static string StripQuery(string uri)
    {
        var end = uri.IndexOfAny(['?', '#']);
        return end < 0 ? uri : uri[..end];
    }

    private static bool HasScheme(string uri) => SchemePattern().IsMatch(uri);

    private static bool IsDrive(string path) => path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path.Length == 2 || path[2] == '/');

    private static string Shown(string text) => SourceFiles.Display(text.Length <= 200 ? text : text[..200] + "…");

    // A scheme of two or more characters, so that a Windows drive such as C: is not one.
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]+:", RegexOptions.CultureInvariant)]
    private static partial Regex SchemePattern();
}
