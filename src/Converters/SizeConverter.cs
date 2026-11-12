using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Diffra.Converters;

/// <summary>One file a converter read: its repository-relative path, the converter and its SHA-256 digest.</summary>
public sealed record ConverterSource(string Path, string Format, string Sha256);

/// <summary>
/// What a converter read: every file it read, and either the entry's <c>data</c> as JSON or the
/// problems that reject the entry.
/// </summary>
public sealed record ConverterResult(IReadOnlyList<ConverterSource> Sources, string? Data, IReadOnlyList<string> Problems);

/// <summary>
/// The <c>files</c> converter for <c>diffra/size@1</c> (docs/datasets.md): the length of every
/// file a glob matches, keyed by repository-relative path, and their total. It does not parse what
/// it reads; it streams each file once to take its length and its SHA-256 digest.
/// </summary>
public static class SizeConverter
{
    /// <summary>The converter's name under <c>from</c> and in each source's <c>format</c>.</summary>
    public const string Name = "files";

    /// <summary>The dataset type it reads.</summary>
    public const string DatasetType = "diffra/size@1";

    /// <summary>The most files one entry may read: the entry schema's limit on sources.</summary>
    public const int MaximumFiles = SourceFiles.MaximumFiles;

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Reads the files <paramref name="glob"/> matches under <paramref name="root"/>. More files
    /// than <paramref name="maximumKeys"/>, the root file's <c>maxKeys</c> budget, rejects the
    /// entry, because the entry schema cannot yet state which keys a budget dropped.
    /// </summary>
    public static ConverterResult Read(string root, string glob, int? maximumKeys)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(glob);
        var problems = new List<string>();
        var match = SourceFiles.Find(root, glob, problems);
        if (match is not null && problems.Count == 0 && maximumKeys is { } budget && match.Files.Count > budget)
        {
            problems.Add($"{match.Files.Count.ToString(CultureInfo.InvariantCulture)} files match '{glob}', more than the root file's maxKeys budget of {budget.ToString(CultureInfo.InvariantCulture)}. "
                + "Keeping only the largest needs the entry to state what was dropped, which the entry schema cannot do yet; narrow the glob or raise maxKeys.");
        }

        return problems.Count > 0 || match is null ? Rejected([], [.. problems]) : Convert(root, match.Files);
    }

    // Reads the given repository-relative files, in ordinal order whatever order they are given
    // in, and returns their sources and data. It trusts its paths: Read checks them first.
    internal static ConverterResult Convert(string root, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(paths);
        var ordered = paths.Distinct(StringComparer.Ordinal).ToList();
        ordered.Sort(StringComparer.Ordinal);
        var sources = new List<ConverterSource>(ordered.Count);
        var lengths = new List<long>(ordered.Count);
        foreach (var path in ordered)
        {
            try
            {
                var (length, digest) = Measure(Path.Combine(root, path));
                sources.Add(new ConverterSource(path, Name, digest));
                lengths.Add(length);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return Rejected(sources, $"'{path}' cannot be read: {exception.Message}");
            }
        }

        if (sources.Count == 0)
        {
            return Rejected(sources, "No file was given to read.");
        }

        return new ConverterResult(sources, Data(sources, lengths), []);
    }

    // The data, with keys in ordinal order: { "files": { path: { "value": n } }, "total": { "value": n } }.
    private static string Data(List<ConverterSource> sources, List<long> lengths)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("files");
            decimal total = 0;
            for (var index = 0; index < sources.Count; index++)
            {
                writer.WriteStartObject(sources[index].Path);
                writer.WriteString("value", lengths[index].ToString(CultureInfo.InvariantCulture));
                writer.WriteEndObject();
                total += lengths[index];
            }

            writer.WriteEndObject();
            writer.WriteStartObject("total");
            writer.WriteString("value", total.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // The length and SHA-256 digest of a regular file, both from one read of its bytes.
    private static (long Length, string Digest) Measure(string path)
    {
        using var stream = SourceFiles.OpenRegular(path);
        var expected = stream.Length;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            length += read;
            if (length > expected)
            {
                break;
            }
        }

        if (length != expected)
        {
            throw new IOException("its length changed while it was read, or it is not a regular file.");
        }

        return (length, System.Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    // The entry schema's rule for a source path (see SourceFiles.IsSafePath).
    internal static bool IsSafePath(string path) => SourceFiles.IsSafePath(path);

    private static ConverterResult Rejected(IReadOnlyList<ConverterSource> sources, params string[] problems) =>
        new(sources, null, problems);
}
