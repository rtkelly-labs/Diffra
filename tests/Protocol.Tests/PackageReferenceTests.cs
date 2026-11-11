using System.Text.Json;
using System.Xml.Linq;

namespace Diffra.Protocol.Tests;

/// <summary>
/// Checks that every package and framework reference in the repository is listed, with a reason,
/// in <c>.config/package-references.json</c>, and that every listed entry is still used. Adding a
/// reference therefore needs an edit to that file, which is where the review happens.
/// </summary>
public static class PackageReferenceTests
{
    private const string ListPath = ".config/package-references.json";

    public static void RunAll()
    {
        var repoRoot = FindRepositoryRoot();
        var found = FindReferences(repoRoot);
        var listed = ReadList(repoRoot);

        var unreviewed = found.Except(listed).ToList();
        var unused = listed.Except(found).ToList();
        if (unreviewed.Count > 0 || unused.Count > 0)
        {
            var lines = unreviewed.Select(r => $"not listed in {ListPath}: {r}")
                .Concat(unused.Select(r => $"listed in {ListPath} but not used: {r}"));
            throw new InvalidOperationException(string.Join(Environment.NewLine, lines));
        }

        ParsesReferencesFromProjectText();
        Console.WriteLine($"PASS every package and framework reference is reviewed ({found.Count} listed)");
    }

    private static void ParsesReferencesFromProjectText()
    {
        const string project = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Example.Package" Version="1.0.0" />
                <FrameworkReference Include="Example.Framework" />
                <PackageReference Update="Updated.Only" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """;
        var references = ReferencesInProject("p.csproj", XDocument.Parse(project)).ToList();
        if (references.Count != 2
            || !references.Contains(new Reference("p.csproj", "PackageReference", "Example.Package"))
            || !references.Contains(new Reference("p.csproj", "FrameworkReference", "Example.Framework")))
        {
            throw new InvalidOperationException($"Unexpected references parsed: {string.Join(", ", references)}");
        }

        var directives = ReferencesInScript("s.cs", ["#:package Example.Tool@1.2.3", "// #:package Not.A.Directive", "#:property X=Y"]).ToList();
        if (directives.Count != 1 || directives[0] != new Reference("s.cs", "PackageReference", "Example.Tool"))
        {
            throw new InvalidOperationException($"Unexpected script directives parsed: {string.Join(", ", directives)}");
        }
    }

    private static HashSet<Reference> FindReferences(string repoRoot)
    {
        var references = new HashSet<Reference>();
        var pending = new Stack<string>();
        pending.Push(repoRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!IgnoredDirectories.Contains(Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                var extension = Path.GetExtension(file);
                if (extension is ".csproj" or ".props" or ".targets")
                {
                    references.UnionWith(ReferencesInProject(relative, XDocument.Load(file)));
                }
                else if (extension == ".cs" && relative.StartsWith("scripts/", StringComparison.Ordinal))
                {
                    references.UnionWith(ReferencesInScript(relative, File.ReadLines(file)));
                }
            }
        }

        return references;
    }

    // Build output, tool caches and local scratch directories. Most are gitignored, so a stray
    // project file in one of them is not part of the repository.
    private static readonly HashSet<string> IgnoredDirectories =
        new(
            [
                "bin", "obj", "Bin", "Obj", ".git", "temp", ".temp", "artifacts", ".tools-cache", "dogfood-run",
                "TestResults", "node_modules", "StrykerOutput", "stryker-output",
            ],
            StringComparer.Ordinal);

    private static IEnumerable<Reference> ReferencesInProject(string project, XDocument document) =>
        document.Descendants()
            .Where(e => e.Name.LocalName is "PackageReference" or "FrameworkReference")
            .Select(e => (Kind: e.Name.LocalName, Name: (string?)e.Attribute("Include")))
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .Select(r => new Reference(project, r.Kind, r.Name!.Trim()));

    private static IEnumerable<Reference> ReferencesInScript(string script, IEnumerable<string> lines) =>
        lines.Where(l => l.StartsWith("#:package ", StringComparison.Ordinal))
            .Select(l => l["#:package ".Length..].Trim().Split('@')[0])
            .Select(name => new Reference(script, "PackageReference", name));

    private static HashSet<Reference> ReadList(string repoRoot)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repoRoot, ListPath)));
        var list = new HashSet<Reference>();
        foreach (var entry in document.RootElement.GetProperty("references").EnumerateArray())
        {
            var reason = entry.GetProperty("reason").GetString();
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new InvalidOperationException($"Every entry in {ListPath} needs a reason: {entry}");
            }

            var reference = new Reference(
                entry.GetProperty("project").GetString()!,
                entry.GetProperty("kind").GetString()!,
                entry.GetProperty("name").GetString()!);
            if (!list.Add(reference))
            {
                throw new InvalidOperationException($"Duplicate entry in {ListPath}: {reference}");
            }
        }

        return list;
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Diffra.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find Diffra.slnx above the test assembly.");
    }

    private sealed record Reference(string Project, string Kind, string Name);
}
