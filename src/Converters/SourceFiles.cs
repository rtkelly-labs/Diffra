using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Diffra.Converters;

/// <summary>
/// What every built-in converter shares when it finds and opens the files a glob names: the glob
/// is relative to the repository root, no link is followed, only regular files are read, and one
/// entry reads at most <see cref="MaximumFiles"/> files (docs/datasets.md, "Untrusted input").
/// </summary>
internal static partial class SourceFiles
{
    /// <summary>The most files one entry may read: the entry schema's limit on sources.</summary>
    public const int MaximumFiles = 1000;

    /// <summary>
    /// The files <paramref name="glob"/> matches under <paramref name="root"/>, in ordinal order.
    /// Every reason they cannot all be read is added to <paramref name="problems"/>: a glob that
    /// leaves the repository, a matched link, a folder that cannot be listed, more files than the
    /// safety limit, no file at all, a count <paramref name="tooMany"/> refuses, or a file name
    /// an entry cannot hold, in that order. Returns <see langword="null"/> when the glob itself is
    /// refused.
    /// </summary>
    public static FileGlobMatch? Find(string root, string glob, List<string> problems, Func<int, string?>? tooMany = null)
    {
        var segments = FileGlob.Parse(glob, out var problem);
        if (segments is null)
        {
            problems.Add(problem);
            return null;
        }

        var match = FileGlob.Match(root, segments, MaximumFiles);
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
        else if (tooMany?.Invoke(match.Files.Count) is { } over)
        {
            problems.Add(over);
        }

        foreach (var path in match.Files.Where(path => !IsSafePath(path)))
        {
            problems.Add($"The file name '{Display(path)}' cannot be recorded: a path must be at most 1000 characters of valid Unicode, with no backslash or control character.");
        }

        return match;
    }

    /// <summary>
    /// Opens a file to read and checks that it is a regular file. A pipe, a socket or a device is
    /// refused rather than read, since reading one may never end.
    /// </summary>
    public static FileStream OpenRegular(string path)
    {
        var stream = Open(path);
        if (!stream.CanSeek)
        {
            stream.Dispose();
            throw new IOException("it is not a regular file, such as a pipe or a device.");
        }

        return stream;
    }

    /// <summary>
    /// Reads all of a regular file's bytes, which must be exactly the length it had when opened.
    /// </summary>
    public static byte[] ReadAll(FileStream stream, long length)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var bytes = new byte[length];
        var read = 0;
        int count;
        while (read < bytes.Length && (count = stream.Read(bytes, read, bytes.Length - read)) > 0)
        {
            read += count;
        }

        if (read != length || stream.ReadByte() >= 0)
        {
            throw new IOException("its length changed while it was read, or it is not a regular file.");
        }

        return bytes;
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

    /// <summary>Text as it can be shown in a problem: a lone surrogate, which JSON cannot hold, becomes U+FFFD.</summary>
    public static string Display(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    /// <summary>
    /// The entry schema's rule for a source path: at most 1000 characters, no backslash and no
    /// control character. The glob already keeps out '.' and '..' segments and empty ones. A lone
    /// surrogate cannot be written as JSON, so a name holding one is refused too.
    /// </summary>
    public static bool IsSafePath(string path) =>
        !HasLoneSurrogate(path)
        && path.EnumerateRunes().Count() <= 1000
        && !path.Contains('\\', StringComparison.Ordinal)
        && !path.StartsWith('~')
        && !path.EnumerateRunes().Any(Rune.IsControl);

    /// <summary>Whether text holds a surrogate that is not half of a pair.</summary>
    public static bool HasLoneSurrogate(string text)
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
}
