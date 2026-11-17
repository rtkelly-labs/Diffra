// Differential harness for line counts (docs/adr/0010-line-counts.md).
//
//   record --scc <path> --tokei <path> --cloc <path> [--work <dir>]
//       Runs the pinned scc, tokei and cloc over every case in tests/line-counts/corpus and writes
//       their counts to tests/line-counts/oracle.json, then rewrites the conformance table.
//   check --scc <path> --tokei <path> --cloc <path> [--work <dir>]
//       Runs the tools again and reports every count that differs from oracle.json. Exits 1 on any
//       difference. This is how a new release of a tool is checked for drift.
//   verify
//       Needs no tool. Checks that every corpus case has a frozen result from each tool and that
//       its digest matches, that every case has a valid ruling in rulings.json, and that the
//       conformance table is current. CI runs this.
//   table
//       Rewrites the conformance table from oracle.json and rulings.json.
//
// A case is stored as <name>.<ext>.case so that no build, linter or language ban treats it as
// source; the harness copies it to <name>.<ext>, alone in a fresh folder, before a tool reads it.
// A tool that reports cloc as a .pl file is run with perl.
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Diffra.LineCounts.Oracle;

var root = FindRepositoryRoot();
var corpus = Path.Combine(root, "tests", "line-counts", "corpus");
var oraclePath = Path.Combine(root, "tests", "line-counts", "oracle.json");
var rulingsPath = Path.Combine(root, "tests", "line-counts", "rulings.json");
var tablePath = Path.Combine(root, "docs", "line-counts-conformance.md");

if (args.Length == 0)
{
    return Usage();
}

var options = ParseOptions(args);
try
{
    return args[0] switch
    {
        "record" => Record(),
        "check" => Check(),
        "verify" => Verify(),
        "table" => WriteTable(),
        _ => Usage(),
    };
}
catch (HarnessException exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 2;
}

int Record()
{
    var oracle = Run();
    File.WriteAllText(oraclePath, OracleJson.Write(oracle));
    Console.WriteLine($"wrote {Relative(oraclePath)}: {oracle.Cases.Count} cases");
    return WriteTable();
}

int Check()
{
    var frozen = OracleJson.Read(File.ReadAllText(oraclePath));
    var fresh = Run();
    var drift = new List<string>();
    foreach (var tool in Tools.All)
    {
        if (frozen.Versions[tool.Name] != fresh.Versions[tool.Name])
        {
            drift.Add($"{tool.Name}: version {frozen.Versions[tool.Name]} frozen, {fresh.Versions[tool.Name]} run");
        }
    }

    var frozenCases = frozen.Cases.ToDictionary(c => c.Path, StringComparer.Ordinal);
    foreach (var fresher in fresh.Cases)
    {
        if (!frozenCases.TryGetValue(fresher.Path, out var old))
        {
            drift.Add($"{fresher.Path}: not in the oracle");
            continue;
        }

        foreach (var tool in Tools.All)
        {
            var before = old.Results[tool.Name];
            var after = fresher.Results[tool.Name];
            if (before != after)
            {
                drift.Add($"{fresher.Path}: {tool.Name} {before} frozen, {after} now");
            }
        }
    }

    foreach (var line in drift)
    {
        Console.WriteLine(line);
    }

    Console.WriteLine(drift.Count == 0 ? "no drift" : $"{drift.Count} differences");
    return drift.Count == 0 ? 0 : 1;
}

int Verify()
{
    var problems = new List<string>();
    var oracle = OracleJson.Read(File.ReadAllText(oraclePath));
    var rulings = Rulings.Read(File.ReadAllText(rulingsPath));
    var cases = CorpusCases();
    var frozen = oracle.Cases.ToDictionary(c => c.Path, StringComparer.Ordinal);
    foreach (var (path, file) in cases)
    {
        if (!frozen.TryGetValue(path, out var result))
        {
            problems.Add($"{path}: no frozen result; run record");
            continue;
        }

        if (result.Sha256 != Sha256(file))
        {
            problems.Add($"{path}: the case changed since it was frozen; run record");
        }

        foreach (var tool in Tools.All)
        {
            if (!result.Results.ContainsKey(tool.Name))
            {
                problems.Add($"{path}: no result from {tool.Name}");
            }
        }

        if (!rulings.TryGetValue(path, out var ruling))
        {
            problems.Add($"{path}: no ruling in rulings.json");
            continue;
        }

        problems.AddRange(ruling.Problems(path, result));
    }

    foreach (var path in frozen.Keys.Where(p => !cases.ContainsKey(p)))
    {
        problems.Add($"{path}: frozen but no longer in the corpus");
    }

    foreach (var path in rulings.Keys.Where(p => !cases.ContainsKey(p)))
    {
        problems.Add($"{path}: ruled on but not in the corpus");
    }

    if (problems.Count == 0)
    {
        var expected = ConformanceTable.Render(oracle, rulings);
        var actual = File.Exists(tablePath) ? File.ReadAllText(tablePath) : string.Empty;
        if (!string.Equals(expected, actual.ReplaceLineEndings("\n"), StringComparison.Ordinal))
        {
            problems.Add($"{Relative(tablePath)} is out of date; run table");
        }
    }

    foreach (var problem in problems)
    {
        Console.WriteLine(problem);
    }

    Console.WriteLine(problems.Count == 0 ? $"PASS line-count oracle: {cases.Count} cases" : $"FAIL line-count oracle: {problems.Count} problems");
    return problems.Count == 0 ? 0 : 1;
}

int WriteTable()
{
    var oracle = OracleJson.Read(File.ReadAllText(oraclePath));
    var rulings = Rulings.Read(File.ReadAllText(rulingsPath));
    File.WriteAllText(tablePath, ConformanceTable.Render(oracle, rulings));
    Console.WriteLine($"wrote {Relative(tablePath)}");
    return 0;
}

Oracle Run()
{
    var paths = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var tool in Tools.All)
    {
        paths[tool.Name] = options.TryGetValue(tool.Name, out var path)
            ? Path.GetFullPath(path)
            : throw new HarnessException($"--{tool.Name} <path> is required");
    }

    var versions = Tools.All.ToDictionary(t => t.Name, t => t.Version, StringComparer.Ordinal);
    foreach (var tool in Tools.All)
    {
        var output = Execute(paths[tool.Name], tool.VersionArguments, root).Output;
        var found = tool.ParseVersion(output);
        if (found != tool.Version && options.ContainsKey("allow-version-drift"))
        {
            versions[tool.Name] = found;
        }
        else if (found != tool.Version)
        {
            throw new HarnessException($"{tool.Name} is {found}, but the oracle is pinned to {tool.Version}");
        }
    }

    // The harness owns only its own child folder of --work, so that a --work naming a folder with
    // other contents (the checkout, a shared temp folder) never loses them.
    var workBase = options.TryGetValue("work", out var given) ? Path.GetFullPath(given) : Path.Combine(root, "temp");
    var work = Path.Combine(workBase, "line-counts-oracle");
    if (Directory.Exists(work))
    {
        Directory.Delete(work, recursive: true);
    }

    var cases = new List<CaseResult>();
    var index = 0;
    foreach (var (path, file) in CorpusCases())
    {
        var folder = Path.Combine(work, index.ToString("D4", CultureInfo.InvariantCulture));
        index++;
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(path);
        var staged = Path.Combine(folder, name);
        File.Copy(file, staged);
        var results = new SortedDictionary<string, ToolResult>(StringComparer.Ordinal);
        foreach (var tool in Tools.All)
        {
            var run = Execute(paths[tool.Name], [.. tool.Arguments, name], folder);
            results[tool.Name] = tool.Parse(run.Output, name);
        }

        // Hash the staged copy the tools counted, not the original, which may have changed since.
        cases.Add(new CaseResult(path, Sha256(staged), CountLines(staged), results));
    }

    return new Oracle(versions, cases);
}

SortedDictionary<string, string> CorpusCases()
{
    var cases = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach (var file in Directory.EnumerateFiles(corpus, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(corpus, file).Replace('\\', '/');
        if (!relative.EndsWith(".case", StringComparison.Ordinal))
        {
            throw new HarnessException($"{relative}: a corpus file must end in .case");
        }

        cases[relative[..^".case".Length]] = file;
    }

    return cases;
}

string Relative(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

static (string Output, int ExitCode) Execute(string tool, IReadOnlyList<string> arguments, string directory)
{
    var start = new ProcessStartInfo
    {
        FileName = tool.EndsWith(".pl", StringComparison.Ordinal) ? "perl" : tool,
        WorkingDirectory = directory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        StandardOutputEncoding = Encoding.UTF8,
    };
    if (tool.EndsWith(".pl", StringComparison.Ordinal))
    {
        start.ArgumentList.Add(tool);
    }

    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }

    using var process = Process.Start(start) ?? throw new HarnessException($"could not start {tool}");
    var error = process.StandardError.ReadToEndAsync();
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new HarnessException($"{tool} {string.Join(' ', arguments)} exited {process.ExitCode}: {error.Result}");
    }

    return (output, process.ExitCode);
}

static string Sha256(string file) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));

// Physical lines as the tools see them: each LF ends a line (a CR before it is part of the
// terminator; a lone CR ends no line in any of the three tools), and text after the last LF is a
// line of its own. Stored beside each case so a reader sees what the tools were counting.
static int CountLines(string file)
{
    var bytes = File.ReadAllBytes(file);
    var lines = bytes.Count(b => b == '\n');
    return bytes.Length > 0 && bytes[^1] != '\n' ? lines + 1 : lines;
}

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Diffra.slnx")))
        {
            return directory.FullName;
        }
    }

    throw new HarnessException("could not locate Diffra.slnx above the current folder");
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 1; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            throw new HarnessException($"unexpected argument '{args[i]}'");
        }

        var name = args[i][2..];
        options[name] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
    }

    return options;
}

static int Usage()
{
    Console.Error.WriteLine("usage: record|check --scc <path> --tokei <path> --cloc <path> [--work <dir>] | verify | table");
    return 2;
}

namespace Diffra.LineCounts.Oracle
{
    internal sealed class HarnessException(string message) : Exception(message);

    /// <summary>One tool's counts for one case, or <c>Counted = false</c> when it reported no file.</summary>
    internal sealed record ToolResult(bool Counted, string? Language, long Code, long Comments, long Blanks)
    {
        public static readonly ToolResult NotCounted = new(false, null, 0, 0, 0);

        public Counts? Counts => Counted ? new Counts(Code, Comments, Blanks) : null;

        public override string ToString() => Counted ? $"{Language} {Counts}" : "not counted";
    }

    /// <summary>A (code, comments, blanks) triple: the unit two tools agree or disagree on.</summary>
    internal sealed record Counts(long Code, long Comments, long Blanks)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Code}/{Comments}/{Blanks}");
    }

    internal sealed record CaseResult(string Path, string Sha256, int Lines, SortedDictionary<string, ToolResult> Results)
    {
        /// <summary>
        /// The triple at least two of the three tools give exactly, or <c>null</c> on a three-way
        /// split. A tool that does not count the file votes for "not counted", so two such tools are
        /// a majority for not counting it, which <see cref="MajorityNotCounted"/> reports.
        /// </summary>
        public Counts? Majority() =>
            Results.Values.Where(r => r.Counted).GroupBy(r => r.Counts).FirstOrDefault(g => g.Count() >= 2)?.Key;

        public bool MajorityNotCounted => Results.Values.Count(r => !r.Counted) >= 2;
    }

    internal sealed record Oracle(Dictionary<string, string> Versions, List<CaseResult> Cases);

    internal sealed record Tool(
        string Name,
        string Version,
        string Release,
        IReadOnlyList<string> VersionArguments,
        IReadOnlyList<string> Arguments,
        Func<string, string> ParseVersion,
        Func<string, string, ToolResult> Parse);

    /// <summary>
    /// The pinned tools and the exact arguments each is run with. Each runs with its own defaults,
    /// as an adopter would see it, except that ignore files are switched off: a case sits alone in a
    /// scratch folder, which may lie under an ignored path.
    /// </summary>
    internal static class Tools
    {
        public static readonly Tool Cloc = new(
            "cloc",
            "2.12",
            "https://github.com/AlDanial/cloc/releases/download/v2.12/cloc-2.12.pl (sha256 621592d34f835ef178115a1643f15ebc8e73ec1d47b5484c46376fc15b78f03e)",
            ["--version"],
            ["--json", "--by-file", "--quiet", "--skip-uniqueness"],
            output => output.Trim(),
            ParseCloc);

        public static readonly Tool Scc = new(
            "scc",
            "4.1.0",
            "https://github.com/boyter/scc/releases/tag/v4.1.0 (each archive's sha256 is in its checksums.txt)",
            ["--version"],
            ["--format", "json", "--by-file", "--no-cocomo", "--no-complexity", "--no-gitignore", "--no-ignore", "--no-scc-ignore"],
            output => output.Trim().Replace("scc version ", string.Empty, StringComparison.Ordinal),
            ParseScc);

        public static readonly Tool Tokei = new(
            "tokei",
            "14.0.0",
            "https://crates.io/crates/tokei/14.0.0 (cargo install tokei --version 14.0.0 --locked; no release binaries are published)",
            ["--version"],
            ["--output", "json", "--hidden", "--no-ignore"],
            output => output.Trim().Split(' ')[1],
            ParseTokei);

        public static readonly IReadOnlyList<Tool> All = [Cloc, Scc, Tokei];

        // cloc: {"header": ..., "<file>": {"blank", "comment", "code", "language"}, "SUM": ...}.
        // A file cloc does not recognise is absent. cloc writes nothing at all when it counts no file.
        private static ToolResult ParseCloc(string output, string name)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return ToolResult.NotCounted;
            }

            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty(name, out var file))
            {
                return ToolResult.NotCounted;
            }

            return new ToolResult(true, file.GetProperty("language").GetString(), file.GetProperty("code").GetInt64(), file.GetProperty("comment").GetInt64(), file.GetProperty("blank").GetInt64());
        }

        // scc: [{"Name": language, "Files": [{"Code", "Comment", "Blank", ...}]}].
        private static ToolResult ParseScc(string output, string name)
        {
            using var document = JsonDocument.Parse(output);
            foreach (var language in document.RootElement.EnumerateArray())
            {
                foreach (var file in language.GetProperty("Files").EnumerateArray())
                {
                    if (file.GetProperty("Filename").GetString() == name)
                    {
                        return new ToolResult(true, file.GetProperty("Language").GetString(), file.GetProperty("Code").GetInt64(), file.GetProperty("Comment").GetInt64(), file.GetProperty("Blank").GetInt64());
                    }
                }
            }

            return ToolResult.NotCounted;
        }

        // tokei: {"<language>": {"reports": [{"name", "stats": {"code", "comments", "blanks", "blobs"}}]}, "Total": ...}.
        // Embedded languages (blobs), such as code in a Markdown fence, are added to the file's
        // counts, so the triple covers every line of the file as the other tools' do.
        private static ToolResult ParseTokei(string output, string name)
        {
            using var document = JsonDocument.Parse(output);
            foreach (var language in document.RootElement.EnumerateObject())
            {
                if (language.Name == "Total")
                {
                    continue;
                }

                foreach (var report in language.Value.GetProperty("reports").EnumerateArray())
                {
                    if (report.GetProperty("name").GetString() == name)
                    {
                        var (code, comments, blanks) = Sum(report.GetProperty("stats"));
                        return new ToolResult(true, language.Name, code, comments, blanks);
                    }
                }
            }

            return ToolResult.NotCounted;
        }

        private static (long Code, long Comments, long Blanks) Sum(JsonElement stats)
        {
            var code = stats.GetProperty("code").GetInt64();
            var comments = stats.GetProperty("comments").GetInt64();
            var blanks = stats.GetProperty("blanks").GetInt64();
            if (stats.TryGetProperty("blobs", out var blobs))
            {
                foreach (var blob in blobs.EnumerateObject())
                {
                    var items = blob.Value.ValueKind == JsonValueKind.Array ? blob.Value.EnumerateArray().ToList() : [blob.Value];
                    foreach (var item in items)
                    {
                        var (c, m, b) = Sum(item);
                        code += c;
                        comments += m;
                        blanks += b;
                    }
                }
            }

            return (code, comments, blanks);
        }
    }
}
