using System.Text;

namespace Diffra.Converters;

/// <summary>
/// The files a glob matches under the repository root, as repository-relative paths with forward
/// slashes in ordinal order. Links are never followed: a linked folder is not searched, and a
/// matched link is listed apart from the files.
/// </summary>
/// <param name="Files">The matched files, in ordinal order. Holds at most the limit plus one.</param>
/// <param name="Links">The matched links, in ordinal order.</param>
/// <param name="LinkedFolders">How many linked folders the glob reached and did not search.</param>
/// <param name="OverLimit">Whether more files matched than the limit; the search stopped there.</param>
/// <param name="Unreadable">The folders the glob reached that could not be listed, in ordinal order.</param>
public sealed record FileGlobMatch(
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Links,
    int LinkedFolders,
    bool OverLimit,
    IReadOnlyList<string> Unreadable);

/// <summary>
/// A small glob matcher over the file system. A glob is relative to the repository root, with
/// segments separated by <c>/</c>. In a segment, <c>*</c> matches any run of characters and
/// <c>?</c> one character, never a <c>/</c>; a segment that is exactly <c>**</c> matches any
/// number of folders, none included, and a final <c>**</c> matches every file below. Matching is
/// ordinal and case-sensitive against the names on disk, whatever the file system. Folders named
/// <c>.git</c> are never searched.
/// </summary>
public static class FileGlob
{
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Checks that a glob is relative to the repository root and stays inside it, and returns its
    /// segments, or <see langword="null"/> with the reason.
    /// </summary>
    public static IReadOnlyList<string>? Parse(string glob, out string problem)
    {
        ArgumentNullException.ThrowIfNull(glob);
        problem = string.Empty;
        var text = glob;
        while (text.StartsWith("./", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        if (text.Length == 0)
        {
            problem = "The glob is empty.";
            return null;
        }

        if (text.Contains('\\', StringComparison.Ordinal))
        {
            problem = $"The glob '{glob}' has a backslash; separate folders with '/'.";
            return null;
        }

        if (text.StartsWith('/') || text.StartsWith('~') || Path.IsPathRooted(text) || (text.Length > 1 && text[1] == ':'))
        {
            problem = $"The glob '{glob}' is not relative to the repository root.";
            return null;
        }

        if (text.EnumerateRunes().Any(Rune.IsControl))
        {
            problem = "The glob has a control character.";
            return null;
        }

        var segments = text.Split('/');
        if (segments.Any(segment => segment is "." or ".."))
        {
            problem = $"The glob '{glob}' has a '.' or '..' segment; it must stay inside the repository.";
            return null;
        }

        if (segments.Any(segment => segment.Length == 0))
        {
            problem = $"The glob '{glob}' has an empty segment or ends with '/'.";
            return null;
        }

        return segments;
    }

    /// <summary>
    /// The files under <paramref name="root"/> that the glob's segments match, stopping once more
    /// than <paramref name="limit"/> files are found.
    /// </summary>
    public static FileGlobMatch Match(string root, IReadOnlyList<string> segments, int limit)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(segments);
        var search = new Search(segments, limit);
        search.Walk(new DirectoryInfo(root), string.Empty, 0);
        var files = search.Files.ToList();
        files.Sort(StringComparer.Ordinal);
        var links = search.Links.ToList();
        links.Sort(StringComparer.Ordinal);
        var unreadable = search.Unreadable.ToList();
        unreadable.Sort(StringComparer.Ordinal);
        return new FileGlobMatch(files, links, search.LinkedFolders.Count, search.OverLimit, unreadable);
    }

    /// <summary>Whether a name matches one glob segment, by <c>*</c> and <c>?</c>, ordinally.</summary>
    public static bool IsMatch(string segment, string name)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(name);
        var pattern = segment.EnumerateRunes().ToArray();
        var text = name.EnumerateRunes().ToArray();
        int p = 0, t = 0, star = -1, resume = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p].Value == '?' || (pattern[p].Value != '*' && pattern[p] == text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p].Value == '*')
            {
                star = p++;
                resume = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++resume;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p].Value == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private sealed class Search(IReadOnlyList<string> segments, int limit)
    {
        public HashSet<string> Files { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Links { get; } = new(StringComparer.Ordinal);

        public HashSet<string> LinkedFolders { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Unreadable { get; } = new(StringComparer.Ordinal);

        public bool OverLimit => Files.Count > limit;

        public void Walk(DirectoryInfo directory, string prefix, int index)
        {
            if (OverLimit)
            {
                return;
            }

            var segment = segments[index];
            var last = index == segments.Count - 1;
            if (segment == "**")
            {
                // None of the folders: the rest of the glob applies here. A final ** matches every
                // file here and below.
                if (last)
                {
                    WalkSegment(directory, prefix, "*", last: true, next: index);
                }
                else
                {
                    Walk(directory, prefix, index + 1);
                }

                foreach (var child in Entries(directory, prefix))
                {
                    if (OverLimit)
                    {
                        return;
                    }

                    if (child is DirectoryInfo folder && folder.Name != ".git")
                    {
                        if (IsLink(folder))
                        {
                            LinkedFolders.Add(prefix + folder.Name);
                        }
                        else
                        {
                            Walk(folder, prefix + folder.Name + "/", index);
                        }
                    }
                }

                return;
            }

            WalkSegment(directory, prefix, segment, last, index + 1);
        }

        private void WalkSegment(DirectoryInfo directory, string prefix, string segment, bool last, int next)
        {
            foreach (var child in Entries(directory, prefix))
            {
                if (OverLimit)
                {
                    return;
                }

                if (!IsMatch(segment, child.Name))
                {
                    continue;
                }

                var path = prefix + child.Name;
                if (last)
                {
                    // A folder is not a file, whether linked or not; a linked file is not followed.
                    if (child is FileInfo)
                    {
                        (IsLink(child) ? Links : Files).Add(path);
                    }
                }
                else if (child is DirectoryInfo folder && folder.Name != ".git")
                {
                    if (IsLink(folder))
                    {
                        LinkedFolders.Add(path);
                    }
                    else
                    {
                        Walk(folder, path + "/", next);
                    }
                }
            }
        }

        // The entries of a folder. A folder that cannot be listed is noted, so that a total is
        // never quietly short; a folder that does not exist simply has none.
        private List<FileSystemInfo> Entries(DirectoryInfo directory, string prefix)
        {
            try
            {
                return directory.EnumerateFileSystemInfos("*", Options).ToList();
            }
            catch (DirectoryNotFoundException)
            {
                return [];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Unreadable.Add(prefix.Length == 0 ? "." : prefix.TrimEnd('/'));
                return [];
            }
        }
    }
}
