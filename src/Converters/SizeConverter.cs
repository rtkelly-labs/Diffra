using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

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
public static partial class SizeConverter
{
    /// <summary>The converter's name under <c>from</c> and in each source's <c>format</c>.</summary>
    public const string Name = "files";

    /// <summary>The dataset type it reads.</summary>
    public const string DatasetType = "diffra/size@1";

    /// <summary>The most files one entry may read: the entry schema's limit on sources.</summary>
    public const int MaximumFiles = 1000;

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
        var segments = FileGlob.Parse(glob, out var problem);
        if (segments is null)
        {
            return Rejected([], problem);
        }

        var match = FileGlob.Match(root, segments, MaximumFiles);
        var problems = new List<string>();
        foreach (var link in match.Links)
        {
            problems.Add($"'{Display(link)}' is a link. Links are not followed, so that no file outside the repository is read; it was not read.");
        }

        foreach (var folder in match.Unreadable)
        {
            problems.Add($"The folder '{Display(folder)}' cannot be listed, so the files the glob matches in it are not known.");
        }

        if (match.OverLimit)
        {
            problems.Add($"More than {MaximumFiles.ToString(CultureInfo.InvariantCulture)} files match '{glob}', the most one entry can read.");
        }
        else if (match.Files.Count == 0 && match.Links.Count == 0 && match.Unreadable.Count == 0)
        {
            var folders = match.LinkedFolders == 0
                ? string.Empty
                : $" {match.LinkedFolders.ToString(CultureInfo.InvariantCulture)} linked {(match.LinkedFolders == 1 ? "folder was" : "folders were")} not searched.";
            problems.Add($"No file matches '{glob}'.{folders}");
        }
        else if (maximumKeys is { } budget && match.Files.Count > budget)
        {
            problems.Add($"{match.Files.Count.ToString(CultureInfo.InvariantCulture)} files match '{glob}', more than the root file's maxKeys budget of {budget.ToString(CultureInfo.InvariantCulture)}. "
                + "Keeping only the largest needs the entry to state what was dropped, which the entry schema cannot do yet; narrow the glob or raise maxKeys.");
        }

        foreach (var path in match.Files.Where(path => !IsSafePath(path)))
        {
            problems.Add($"The file name '{Display(path)}' cannot be recorded: a path must be at most 1000 characters of valid Unicode, with no backslash or control character.");
        }

        return problems.Count > 0 ? Rejected([], [.. problems]) : Convert(root, match.Files);
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

    // The length and SHA-256 digest of a regular file, both from one read of its bytes. A pipe,
    // a socket or a device is refused rather than read, since reading one may never end.
    private static (long Length, string Digest) Measure(string path)
    {
        using var stream = Open(path);
        if (!stream.CanSeek)
        {
            throw new IOException("it is not a regular file, such as a pipe or a device.");
        }

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

    // Opens a file to read. On Unix it is opened without blocking, because opening a named pipe
    // to read waits for a writer; a pipe then shows as a stream that cannot seek.
    private static FileStream Open(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        }

        var nonBlocking = OperatingSystem.IsLinux() ? 0x800 : 0x4;
        var descriptor = OpenNative([.. Encoding.UTF8.GetBytes(path), 0], nonBlocking);
        if (descriptor < 0)
        {
            throw new IOException(Marshal.GetLastPInvokeErrorMessage());
        }

        return new FileStream(new SafeFileHandle(descriptor, ownsHandle: true), FileAccess.Read, 1);
    }

    // open(2) with O_RDONLY (0) and the given flags. No mode is passed, since O_CREAT is not set.
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    private static partial int OpenNative(byte[] path, int flags);

    // A path as it can be shown in a problem: a lone surrogate, which JSON cannot hold, becomes U+FFFD.
    private static string Display(string path)
    {
        var builder = new StringBuilder(path.Length);
        foreach (var rune in path.EnumerateRunes())
        {
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    // The entry schema's rule for a source path: at most 1000 characters, no backslash and no
    // control character. The glob already keeps out '.' and '..' segments and empty ones. A lone
    // surrogate cannot be written as JSON, so a name holding one is refused too.
    internal static bool IsSafePath(string path) =>
        !HasLoneSurrogate(path)
        && path.EnumerateRunes().Count() <= 1000
        && !path.Contains('\\', StringComparison.Ordinal)
        && !path.StartsWith('~')
        && !path.EnumerateRunes().Any(Rune.IsControl);

    private static bool HasLoneSurrogate(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                index++;
            }
            else if (char.IsSurrogate(text[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static ConverterResult Rejected(IReadOnlyList<ConverterSource> sources, params string[] problems) =>
        new(sources, null, problems);
}
