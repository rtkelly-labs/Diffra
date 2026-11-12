using System.Diagnostics;
using System.Security.Cryptography;
using Diffra.Converters;

namespace Diffra.Protocol.Tests;

/// <summary>
/// Checks the <c>files</c> converter for <c>diffra/size@1</c> and its glob matcher: the length and
/// digest of each matched file, keys in ordinal order whatever order the files are found in, links
/// not followed, and the entry rejected when nothing matches, when a link is matched, or when more
/// files match than the safety limit or the <c>maxKeys</c> budget.
/// </summary>
public static class SizeConverterTests
{
    public static void RunAll()
    {
        MatchesSegments();
        ParsesGlobs();
        TotalsTheMatchedFiles();
        WritesKeysInOrdinalOrder();
        MatchesAnyDepthWithTwoStars();
        MatchesNamesAsTheyAreOnDisk();
        MatchesDotfiles();
        RecordsEachFilesDigest();
        GivesTheSameDataInAnyOrder();
        RejectsAGlobThatMatchesNothing();
        DoesNotFollowLinks();
        RejectsAFolderItCannotList();
        RejectsAFileThatIsNotRegular();
        RefusesALoneSurrogate();
        RejectsMoreFilesThanTheLimit();
        RejectsMoreFilesThanTheBudget();
        Console.WriteLine("PASS size converter");
    }

    private static void MatchesSegments()
    {
        foreach (var (segment, name, expected) in new[]
        {
            ("*.bin", "a.bin", true), ("*.bin", "a.bin.txt", false), ("a?c", "abc", true), ("a?c", "ac", false),
            ("a*b*c", "aXXbYc", true), ("a*b*c", "aXXbY", false), ("*", ".hidden", true), ("x", "X", false),
            ("é?", "éü", true), ("?", "\U0001F600", true), ("**", "anything", true),
        })
        {
            if (FileGlob.IsMatch(segment, name) != expected)
            {
                throw new InvalidOperationException($"Expected '{segment}' {(expected ? "to match" : "not to match")} '{name}'.");
            }
        }
    }

    private static void ParsesGlobs()
    {
        Equal("a/*.bin", string.Join("/", FileGlob.Parse("./a/*.bin", out _)!));
        Equal("a/**/b", string.Join("/", FileGlob.Parse("a/**/**/b", out _)!));
        Equal("**", string.Join("/", FileGlob.Parse("**/**", out _)!));

        // A name with a colon is a name on Unix; a drive is refused everywhere.
        Equal(!OperatingSystem.IsWindows(), FileGlob.Parse("a:b/x", out _) is not null);
        foreach (var glob in new[] { "", "/etc/passwd", "~/x", "C:/x", "C:", "c:/x", "../x", "a/../b", "a/./b", "a\\b", "a//b", "a/", "a\u0001" })
        {
            if (FileGlob.Parse(glob, out var problem) is not null || problem.Length == 0)
            {
                throw new InvalidOperationException($"Expected the glob '{glob}' to be refused.");
            }
        }
    }

    private static void TotalsTheMatchedFiles()
    {
        using var folder = new Folder();
        folder.Write("artifacts/a.nupkg", 10);
        folder.Write("artifacts/b.nupkg", 3);
        folder.Write("artifacts/empty.nupkg", 0);
        folder.Write("artifacts/notes.txt", 99);
        var result = SizeConverter.Read(folder.Root, "artifacts/*.nupkg", maximumKeys: null);
        Equal(0, result.Problems.Count);
        Equal(
            """{"files":{"artifacts/a.nupkg":{"value":"10"},"artifacts/b.nupkg":{"value":"3"},"artifacts/empty.nupkg":{"value":"0"}},"total":{"value":"13"}}""",
            result.Data);
        Equal("artifacts/a.nupkg,artifacts/b.nupkg,artifacts/empty.nupkg", string.Join(",", result.Sources.Select(source => source.Path)));
        Equal(true, result.Sources.All(source => source.Format == "files"));
    }

    private static void WritesKeysInOrdinalOrder()
    {
        using var folder = new Folder();
        foreach (var path in new[] { "a/b.bin", "a.bin", "a-b/c.bin", "B.bin", "_.bin" })
        {
            folder.Write(path, 1);
        }

        var result = SizeConverter.Read(folder.Root, "**/*.bin", maximumKeys: null);
        Equal("B.bin,_.bin,a-b/c.bin,a.bin,a/b.bin", string.Join(",", result.Sources.Select(source => source.Path)));
        Equal(
            """{"files":{"B.bin":{"value":"1"},"_.bin":{"value":"1"},"a-b/c.bin":{"value":"1"},"a.bin":{"value":"1"},"a/b.bin":{"value":"1"}},"total":{"value":"5"}}""",
            result.Data);
    }

    private static void MatchesAnyDepthWithTwoStars()
    {
        using var folder = new Folder();
        folder.Write("pkg/top.nupkg", 1);
        folder.Write("pkg/a/b/deep.nupkg", 2);
        folder.Write("pkg/a/readme.md", 4);
        folder.Write("other/x.nupkg", 8);
        folder.Write(".git/objects/y.nupkg", 16);
        Equal("pkg/a/b/deep.nupkg,pkg/top.nupkg", Paths(folder, "pkg/**/*.nupkg"));
        Equal("pkg/a/b/deep.nupkg,pkg/a/readme.md,pkg/top.nupkg", Paths(folder, "pkg/**"));
        Equal("other/x.nupkg,pkg/a/b/deep.nupkg,pkg/top.nupkg", Paths(folder, "**/*.nupkg"));
        Equal("pkg/a/b/deep.nupkg", Paths(folder, "pkg/*/*/*.nupkg"));
        Equal("pkg/a/b/deep.nupkg", Paths(folder, "pkg/**/b/**/de?p.nupkg"));
        Equal("other/x.nupkg,pkg/a/b/deep.nupkg,pkg/top.nupkg", Paths(folder, "**/**/*.nupkg"));

        // A .git file, as a linked worktree or a submodule has, is never matched either.
        folder.Write("pkg/sub/.git", 32);
        Equal("pkg/a/b/deep.nupkg,pkg/a/readme.md,pkg/top.nupkg", Paths(folder, "pkg/**"));
        Equal("No file matches 'pkg/sub/.git'.", SizeConverter.Read(folder.Root, "pkg/sub/.git", maximumKeys: null).Problems.Single());
    }

    private static void MatchesDotfiles()
    {
        using var folder = new Folder();
        folder.Write("out/.hidden.bin", 1);
        folder.Write("out/a.bin", 2);
        folder.Write(".cache/c.bin", 4);
        Equal("out/.hidden.bin,out/a.bin", Paths(folder, "out/*.bin"));
        Equal(".cache/c.bin,out/.hidden.bin,out/a.bin", Paths(folder, "**/*.bin"));
    }

    private static void MatchesNamesAsTheyAreOnDisk()
    {
        using var folder = new Folder();
        folder.Write("Dist/App.zip", 1);

        // Matching is ordinal against the names on disk, so a case-insensitive file system gives
        // the same result as any other.
        Equal("Dist/App.zip", Paths(folder, "Dist/*.zip"));
        Equal(true, SizeConverter.Read(folder.Root, "dist/app.zip", maximumKeys: null).Problems[0].StartsWith("No file matches", StringComparison.Ordinal));
    }

    private static void RecordsEachFilesDigest()
    {
        using var folder = new Folder();
        var bytes = folder.Write("out/data.bin", 200_000);
        folder.Write("out/empty.bin", 0);
        var result = SizeConverter.Read(folder.Root, "out/*.bin", maximumKeys: null);
        Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), result.Sources[0].Sha256);
        Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", result.Sources[1].Sha256);
        Equal("""{"files":{"out/data.bin":{"value":"200000"},"out/empty.bin":{"value":"0"}},"total":{"value":"200000"}}""", result.Data);
    }

    private static void GivesTheSameDataInAnyOrder()
    {
        // Files created in opposite orders, which many file systems list in creation order.
        string[] names = ["z.bin", "m/a.bin", "a.bin", "B.bin", "m.bin"];
        using var first = new Folder();
        using var second = new Folder();
        for (var index = 0; index < names.Length; index++)
        {
            first.Write(names[index], index + 1);
            second.Write(names[^(index + 1)], names.Length - index);
        }

        var expected = SizeConverter.Read(first.Root, "**", maximumKeys: null);
        Equal(expected.Data, SizeConverter.Read(second.Root, "**", maximumKeys: null).Data);

        // Whatever order the matched paths reach the converter in, the sources and data are the same.
        var random = new Random(7);
        for (var round = 0; round < 10; round++)
        {
            var shuffled = names.OrderBy(_ => random.Next()).ToList();
            var result = SizeConverter.Convert(first.Root, shuffled);
            Equal(expected.Data, result.Data);
            Equal(string.Join(",", expected.Sources), string.Join(",", result.Sources));
        }
    }

    private static void RejectsAGlobThatMatchesNothing()
    {
        using var folder = new Folder();
        folder.Write("a.txt", 1);
        var result = SizeConverter.Read(folder.Root, "dist/*.zip", maximumKeys: null);
        Equal(null, result.Data);
        Equal(0, result.Sources.Count);
        Equal("No file matches 'dist/*.zip'.", result.Problems[0]);

        var outside = SizeConverter.Read(folder.Root, "../*.txt", maximumKeys: null);
        Equal(true, outside.Problems[0].Contains("must stay inside the repository", StringComparison.Ordinal));
    }

    private static void DoesNotFollowLinks()
    {
        using var outside = new Folder();
        outside.Write("secret.bin", 5);
        outside.Write("folder/inner.bin", 6);
        using var folder = new Folder();
        folder.Write("out/real.bin", 1);
        try
        {
            File.CreateSymbolicLink(Path.Combine(folder.Root, "out", "secret.bin"), Path.Combine(outside.Root, "secret.bin"));
            Directory.CreateSymbolicLink(Path.Combine(folder.Root, "linked"), Path.Combine(outside.Root, "folder"));
        }
        catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException)
        {
            // Creating a link on Windows needs a privilege or developer mode the runner may not have.
            Console.WriteLine("SKIP size converter links: links cannot be created here.");
            return;
        }

        // A matched link rejects the entry, and the file it points to is not read.
        var linked = SizeConverter.Read(folder.Root, "out/*.bin", maximumKeys: null);
        Equal(null, linked.Data);
        Equal(0, linked.Sources.Count);
        Equal(true, linked.Problems.Single().StartsWith("'out/secret.bin' is a link.", StringComparison.Ordinal));

        // A linked folder is not searched.
        Equal("No file matches 'linked/*.bin'. 1 linked folder was not searched.", SizeConverter.Read(folder.Root, "linked/*.bin", maximumKeys: null).Problems.Single());
        Equal("No file matches '**/inner.bin'. 1 linked folder was not searched.", SizeConverter.Read(folder.Root, "**/inner.bin", maximumKeys: null).Problems.Single());
        Equal("No file matches 'linked/**'. 1 linked folder was not searched.", SizeConverter.Read(folder.Root, "linked/**", maximumKeys: null).Problems.Single());
        Equal("No file matches '*/inner.bin'. 1 linked folder was not searched.", SizeConverter.Read(folder.Root, "*/inner.bin", maximumKeys: null).Problems.Single());
        Equal("out/real.bin", Paths(folder, "**/real.bin"));

        // A file link matched under ** rejects the entry too.
        var any = SizeConverter.Read(folder.Root, "**/*.bin", maximumKeys: null);
        Equal(null, any.Data);
        Equal(true, any.Problems.Single().StartsWith("'out/secret.bin' is a link.", StringComparison.Ordinal));

        // A link that points inside the repository is not followed either.
        using var inside = new Folder();
        inside.Write("out/real.bin", 1);
        File.CreateSymbolicLink(Path.Combine(inside.Root, "out", "alias.bin"), Path.Combine(inside.Root, "out", "real.bin"));
        Directory.CreateSymbolicLink(Path.Combine(inside.Root, "again"), Path.Combine(inside.Root, "out"));
        var alias = SizeConverter.Read(inside.Root, "out/*.bin", maximumKeys: null);
        Equal(null, alias.Data);
        Equal(true, alias.Problems.Single().StartsWith("'out/alias.bin' is a link.", StringComparison.Ordinal));
        Equal("No file matches 'again/real.bin'. 1 linked folder was not searched.", SizeConverter.Read(inside.Root, "again/real.bin", maximumKeys: null).Problems.Single());
    }

    private static void RejectsAFolderItCannotList()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var folder = new Folder();
        folder.Write("out/a.bin", 1);
        folder.Write("out/locked/b.bin", 2);
        var locked = Path.Combine(folder.Root, "out", "locked");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            // A user who can list the folder anyway, such as root, has nothing to check.
            var listable = true;
            try
            {
                _ = Directory.GetFileSystemEntries(locked);
            }
            catch (UnauthorizedAccessException)
            {
                listable = false;
            }

            if (!listable)
            {
                var result = SizeConverter.Read(folder.Root, "out/**/*.bin", maximumKeys: null);
                Equal(null, result.Data);
                Equal("The folder 'out/locked' cannot be listed, so the files the glob matches in it are not known.", result.Problems.Single());
            }
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // A named pipe is not read, since reading one waits for a writer; the entry is rejected.
    private static void RejectsAFileThatIsNotRegular()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var folder = new Folder();
        folder.Write("out/a.bin", 1);
        using (var mkfifo = Process.Start(new ProcessStartInfo("mkfifo", [Path.Combine(folder.Root, "out", "pipe.bin")]) { UseShellExecute = false }))
        {
            mkfifo!.WaitForExit();
            Equal(0, mkfifo.ExitCode);
        }

        var result = SizeConverter.Read(folder.Root, "out/*.bin", maximumKeys: null);
        Equal(null, result.Data);
        Equal("'out/pipe.bin' cannot be read: it is not a regular file, such as a pipe or a device.", result.Problems.Single());
    }

    private static void RefusesALoneSurrogate()
    {
        Equal(true, SizeConverter.IsSafePath("a/\U0001F600.bin"));
        Equal(false, SizeConverter.IsSafePath("a/\uD800.bin"));
        Equal(false, SizeConverter.IsSafePath("a/\uDC00x.bin"));
        Equal(false, SizeConverter.IsSafePath("a/x\uD83D"));
    }

    private static void RejectsMoreFilesThanTheLimit()
    {
        using var folder = new Folder();
        for (var index = 0; index < SizeConverter.MaximumFiles; index++)
        {
            folder.Write($"many/{index:D4}.bin", 0);
        }

        var atLimit = SizeConverter.Read(folder.Root, "many/*.bin", maximumKeys: null);
        Equal(0, atLimit.Problems.Count);
        Equal(1000, atLimit.Sources.Count);

        folder.Write("many/1000.bin", 0);
        var over = SizeConverter.Read(folder.Root, "many/*.bin", maximumKeys: null);
        Equal(null, over.Data);
        Equal(0, over.Sources.Count);
        Equal("More than 1000 files match 'many/*.bin', the most one entry can read.", over.Problems.Single());
    }

    private static void RejectsMoreFilesThanTheBudget()
    {
        using var folder = new Folder();
        folder.Write("a.bin", 1);
        folder.Write("b.bin", 2);
        Equal(0, SizeConverter.Read(folder.Root, "*.bin", maximumKeys: 2).Problems.Count);
        var over = SizeConverter.Read(folder.Root, "*.bin", maximumKeys: 1);
        Equal(null, over.Data);
        Equal(true, over.Problems.Single().StartsWith("2 files match '*.bin', more than the root file's maxKeys budget of 1.", StringComparison.Ordinal));

        // The budget is stated with the other problems: after a link, before a name that cannot
        // be recorded.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        folder.Write("c\u0001.bin", 3);
        File.CreateSymbolicLink(Path.Combine(folder.Root, "d.bin"), Path.Combine(folder.Root, "a.bin"));
        var all = SizeConverter.Read(folder.Root, "*.bin", maximumKeys: 1);
        Equal(null, all.Data);
        Equal(3, all.Problems.Count);
        Equal(true, all.Problems[0].StartsWith("'d.bin' is a link.", StringComparison.Ordinal));
        Equal(true, all.Problems[1].StartsWith("3 files match '*.bin', more than the root file's maxKeys budget of 1.", StringComparison.Ordinal));
        Equal(true, all.Problems[2].StartsWith("The file name 'c\u0001.bin' cannot be recorded", StringComparison.Ordinal));
    }

    private static string Paths(Folder folder, string glob)
    {
        var result = SizeConverter.Read(folder.Root, glob, maximumKeys: null);
        if (result.Problems.Count > 0)
        {
            throw new InvalidOperationException($"'{glob}' was rejected: {string.Join(" ", result.Problems)}");
        }

        return string.Join(",", result.Sources.Select(source => source.Path));
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private sealed class Folder : IDisposable
    {
        public Folder()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Diffra.slnx")))
            {
                repository = repository.Parent;
            }

            Root = Path.Combine(repository?.FullName ?? Path.GetTempPath(), "temp", $"size-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public byte[] Write(string path, int length)
        {
            var full = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var bytes = new byte[length];
            for (var index = 0; index < length; index++)
            {
                bytes[index] = (byte)(index * 31 + path.Length);
            }

            File.WriteAllBytes(full, bytes);
            return bytes;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary folder is harmless.
            }
        }
    }
}
