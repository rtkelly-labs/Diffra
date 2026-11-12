using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Diffra.Converters;

namespace Diffra.Protocol.Tests;

/// <summary>
/// Checks the <c>sarif</c> converter for <c>diffra/findings@1</c>: findings from several runs,
/// tools and files, keyed by tool, rule, path and fingerprint; SARIF's level rules; duplicate
/// results kept once with their occurrences; paths made relative to the repository root; the same
/// data whatever order the input comes in; and the entry rejected for malformed or unsupported
/// input, input past the safety limits, or more findings than the <c>maxKeys</c> budget.
/// </summary>
public static class SarifConverterTests
{
    public static void RunAll()
    {
        ReadsSeveralRunsAndTools();
        CombinesSeveralFiles();
        RecordsNoFindings();
        MapsLevels();
        FindsRulesByIndexAndExtension();
        KeysByFingerprint();
        KeepsDuplicatesOnceWithOccurrences();
        GivesTheSameDataInAnyOrder();
        ResolvesPaths();
        ReadsMessageStrings();
        CleansAndCutsMessages();
        RejectsMalformedInput();
        RejectsNestingPastTheLimit();
        RejectsFilesPastTheByteLimits();
        RejectsMoreFindingsThanTheBudget();
        ReadsARealisticCodeQlLog();
        ReadsARealisticRuffLog();
        Console.WriteLine("PASS sarif converter");
    }

    private static void ReadsSeveralRunsAndTools()
    {
        using var folder = new Folder();
        folder.Write("out/lint.sarif", Log(
            Run("Ruff", Result("F401", "error", "src/a.py", 3, "`os` imported but unused"), Result("E501", "warning", "src/b.py", 10, "Line too long")),
            Run("Biome", Result("lint/style/useConst", "note", "web/app.ts", 7, "Use const."))));
        var result = SarifConverter.Read(folder.Root, "out/*.sarif", maximumKeys: null, maximumTextLength: null);
        Equal(0, result.Problems.Count);
        Equal("out/lint.sarif", result.Sources.Single().Path);
        Equal("sarif", result.Sources.Single().Format);
        Equal(Hex(File.ReadAllBytes(Path.Combine(folder.Root, "out/lint.sarif"))), result.Sources.Single().Sha256);
        Equal(
            "{\"byRule\":{\"Biome/lint/style/useConst\":{\"value\":\"1\"},\"Ruff/E501\":{\"value\":\"1\"},\"Ruff/F401\":{\"value\":\"1\"}},"
            + "\"results\":{"
            + $"\"Biome/lint/style/useConst/web/app.ts/{Digest("Use const.")}\":{{\"value\":{{\"level\":\"note\",\"line\":\"7\",\"message\":\"Use const.\",\"path\":\"web/app.ts\",\"rule\":\"lint/style/useConst\",\"tool\":\"Biome\"}}}},"
            + $"\"Ruff/E501/src/b.py/{Digest("Line too long")}\":{{\"value\":{{\"level\":\"warning\",\"line\":\"10\",\"message\":\"Line too long\",\"path\":\"src/b.py\",\"rule\":\"E501\",\"tool\":\"Ruff\"}}}},"
            + $"\"Ruff/F401/src/a.py/{Digest("`os` imported but unused")}\":{{\"value\":{{\"level\":\"error\",\"line\":\"3\",\"message\":\"`os` imported but unused\",\"path\":\"src/a.py\",\"rule\":\"F401\",\"tool\":\"Ruff\"}}}}"
            + "},\"total\":{\"value\":\"3\"}}",
            result.Data);
    }

    private static void CombinesSeveralFiles()
    {
        using var folder = new Folder();
        folder.Write("reports/b/ruff.sarif", Log(Run("Ruff", Result("F401", "error", "a.py", 1, "unused"))));
        folder.Write("reports/a.sarif", Log(Run("Ruff", Result("F401", "error", "b.py", 1, "unused"))));
        folder.Write("reports/notes.txt", "not read");
        var result = SarifConverter.Read(folder.Root, "reports/**/*.sarif", maximumKeys: null, maximumTextLength: null);
        Equal(0, result.Problems.Count);
        Equal("reports/a.sarif,reports/b/ruff.sarif", string.Join(",", result.Sources.Select(source => source.Path)));
        using var data = JsonDocument.Parse(result.Data!);
        Equal("2", data.RootElement.GetProperty("total").GetProperty("value").GetString());
        Equal("2", data.RootElement.GetProperty("byRule").GetProperty("Ruff/F401").GetProperty("value").GetString());
        Equal(2, data.RootElement.GetProperty("results").EnumerateObject().Count());
    }

    private static void RecordsNoFindings()
    {
        using var folder = new Folder();
        folder.Write("empty.sarif", """{"version":"2.1.0","runs":[]}""");
        folder.Write("clean.sarif", Log(Run("Ruff")));
        folder.Write("absent.sarif", """{"version":"2.1.0","runs":[{"tool":{"driver":{"name":"Ruff"}},"results":null}]}""");
        var result = SarifConverter.Read(folder.Root, "*.sarif", maximumKeys: null, maximumTextLength: null);
        Equal(0, result.Problems.Count);
        Equal(3, result.Sources.Count);
        Equal("""{"byRule":{},"results":{},"total":{"value":"0"}}""", result.Data);
    }

    // The result's level when given; otherwise the rule's default level or warning when the kind
    // is absent or fail, and none for any other kind.
    private static void MapsLevels()
    {
        const string rules = """[{"id":"R1","defaultConfiguration":{"level":"error"}},{"id":"R2"}]""";
        var results = new[]
        {
            """{"ruleId":"R1","level":"note","message":{"text":"given"}}""",
            """{"ruleId":"R1","message":{"text":"default"}}""",
            """{"ruleId":"R1","kind":"fail","message":{"text":"fail kind"}}""",
            """{"ruleId":"R1","kind":"pass","message":{"text":"pass kind"}}""",
            """{"ruleId":"R1","kind":"informational","message":{"text":"informational kind"}}""",
            """{"ruleId":"R1","kind":"pass","level":"error","message":{"text":"pass with level"}}""",
            """{"ruleId":"R2","message":{"text":"no default"}}""",
            """{"ruleId":"R3","message":{"text":"no rule metadata"}}""",
        };
        var levels = Levels(Log(RunWithRules("Tool", rules, results)));
        Equal("given=note,default=error,fail kind=error,pass kind=none,informational kind=none,pass with level=error,no default=warning,no rule metadata=warning", levels);

        // A run with no rules at all falls through to warning too.
        Equal("bare=warning", Levels(Log(Run("Tool", """{"ruleId":"X","message":{"text":"bare"}}"""))));

        using var folder = new Folder();
        folder.Write("bad.sarif", Log(Run("Tool", """{"ruleId":"X","level":"fatal","message":{"text":"m"}}""")));
        var rejected = SarifConverter.Read(folder.Root, "bad.sarif", maximumKeys: null, maximumTextLength: null);
        Equal(null, rejected.Data);
        Equal("'bad.sarif' run 0 result 0: the level 'fatal' is not one of error, warning, note, none.", rejected.Problems.Single());
    }

    // A rule descriptor is found by rule.index or ruleIndex, in the extension rule.toolComponent
    // names, and a result with no ruleId takes its rule's id.
    private static void FindsRulesByIndexAndExtension()
    {
        var run = """
            {"tool":{"driver":{"name":"CodeQL","rules":[{"id":"driver-rule","defaultConfiguration":{"level":"note"}}]},
              "extensions":[{"name":"pack","rules":[{"id":"js/x","defaultConfiguration":{"level":"error"}}]}]},
             "results":[
              {"ruleIndex":0,"message":{"text":"by ruleIndex"}},
              {"rule":{"index":0,"toolComponent":{"index":0}},"message":{"text":"by extension"}},
              {"ruleId":"js/x","rule":{"id":"js/x","index":0,"toolComponent":{"index":0}},"message":{"text":"by both"}},
              {"ruleId":"driver-rule","message":{"text":"by id"}}]}
            """;
        using var folder = new Folder();
        folder.Write("codeql.sarif", Log(run));
        var result = SarifConverter.Read(folder.Root, "codeql.sarif", maximumKeys: null, maximumTextLength: null);
        Equal(0, result.Problems.Count);
        Equal("by both=error js/x,by extension=error js/x,by id=note driver-rule,by ruleIndex=note driver-rule", string.Join(",", Findings(result.Data!)
            .Select(finding => $"{finding.GetProperty("message").GetString()}={finding.GetProperty("level").GetString()} {finding.GetProperty("rule").GetString()}")
            .Order(StringComparer.Ordinal)));

        folder.Write("norule.sarif", Log(Run("Tool", """{"message":{"text":"m"}}""")));
        Equal("'norule.sarif' run 0 result 0: the result names no rule: it has no ruleId, rule.id or rule descriptor with an id.",
            SarifConverter.Read(folder.Root, "norule.sarif", maximumKeys: null, maximumTextLength: null).Problems.Single());
    }

    // The fingerprint is the ordinally first name's value in fingerprints, then in
    // partialFingerprints, then a SHA-256 of the message with digits removed. The line is data, not
    // identity.
    private static void KeysByFingerprint()
    {
        var log = Log(Run("Tool",
            """{"ruleId":"R","message":{"text":"a"},"fingerprints":{"b":"second","a/v1":"first"},"partialFingerprints":{"x":"ignored"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"f.cs"}}}]}""",
            """{"ruleId":"R","message":{"text":"b"},"partialFingerprints":{"z":"last","primaryLocationLineHash":"abc:1"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"f.cs"}}}]}""",
            """{"ruleId":"R","message":{"text":"c"},"fingerprints":{},"partialFingerprints":{"only":"partial"}}"""));
        Equal("Tool/R/<none>/partial,Tool/R/f.cs/abc:1,Tool/R/f.cs/first", Keys(log));

        // Without fingerprints, the message with its digits removed: a count or line in the message
        // and a moved line keep the finding the same.
        var moved = Log(Run("Tool",
            Result("R", "warning", "f.cs", 10, "Method has 12 parameters"),
            Result("R", "warning", "f.cs", 40, "Method has 13 parameters")));
        Equal($"Tool/R/f.cs/{Digest("Method has  parameters")}", Keys(moved));
        Equal(Hex(Encoding.UTF8.GetBytes("Method has  parameters")), Digest("Method has 12 parameters"));

        using var folder = new Folder();
        folder.Write("bad.sarif", Log(Run("Tool", """{"ruleId":"R","message":{"text":"m"},"fingerprints":{"a":1}}""")));
        Equal("'bad.sarif' run 0 result 0: the fingerprints value 'a' is not a string.",
            SarifConverter.Read(folder.Root, "bad.sarif", maximumKeys: null, maximumTextLength: null).Problems.Single());
    }

    // Results that share a key are kept once: the most severe, then the lowest line, then the
    // message, with the number of results as occurrences. total and byRule count every result.
    private static void KeepsDuplicatesOnceWithOccurrences()
    {
        var log = Log(Run("Tool",
            """{"ruleId":"R","level":"note","message":{"text":"one"},"fingerprints":{"f":"same"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"f.cs"},"region":{"startLine":2}}}]}""",
            """{"ruleId":"R","level":"error","message":{"text":"two"},"fingerprints":{"f":"same"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"f.cs"},"region":{"startLine":9}}}]}""",
            """{"ruleId":"R","level":"error","message":{"text":"three"},"fingerprints":{"f":"same"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"f.cs"},"region":{"startLine":5}}}]}""",
            """{"ruleId":"R","level":"error","message":{"text":"four"},"fingerprints":{"f":"same"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"f.cs"}}}]}""",
            """{"ruleId":"R","level":"warning","message":{"text":"other"},"fingerprints":{"f":"different"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"f.cs"}}}]}"""));
        Equal(
            """{"byRule":{"Tool/R":{"value":"5"}},"results":{"Tool/R/f.cs/different":{"value":{"level":"warning","message":"other","path":"f.cs","rule":"R","tool":"Tool"}},"""
            + "\"Tool/R/f.cs/same\":{\"value\":{\"level\":\"error\",\"line\":\"5\",\"message\":\"three\",\"occurrences\":\"4\",\"path\":\"f.cs\",\"rule\":\"R\",\"tool\":\"Tool\"}}},\"total\":{\"value\":\"5\"}}",
            Data(log));
    }

    private static void GivesTheSameDataInAnyOrder()
    {
        var results = new List<string>
        {
            Result("B", "warning", "z.py", 1, "same"),
            Result("B", "error", "z.py", 7, "same"),
            Result("B", "error", "z.py", 3, "same"),
            Result("A", "note", "a.py", 2, "x"),
            Result("a", "note", "a.py", 2, "x"),
            Result("A", "note", "A.py", 2, "y"),
            """{"ruleId":"B","level":"error","message":{"text":"same"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"z.py"}}}]}""",
            Result("B", "error", "z.py", 3, "same"),
        };
        var expected = Data(Log(Run("Tool", [.. results]), Run("Other", Result("A", "note", "a.py", 1, "x"))));
        var random = new Random(11);
        for (var round = 0; round < 10; round++)
        {
            var shuffled = results.OrderBy(_ => random.Next()).ToArray();
            Equal(expected, Data(Log(Run("Other", Result("A", "note", "a.py", 1, "x")), Run("Tool", shuffled))));
        }

        // The same results split over files found in different orders give the same data.
        using var first = new Folder();
        first.Write("z.sarif", Log(Run("Tool", [.. results.Take(4)])));
        first.Write("a.sarif", Log(Run("Tool", [.. results.Skip(4)]), Run("Other", Result("A", "note", "a.py", 1, "x"))));
        using var second = new Folder();
        second.Write("a.sarif", Log(Run("Tool", [.. results.Skip(4)]), Run("Other", Result("A", "note", "a.py", 1, "x"))));
        second.Write("z.sarif", Log(Run("Tool", [.. results.Take(4)])));
        Equal(expected, SarifConverter.Read(first.Root, "*.sarif", maximumKeys: null, maximumTextLength: null).Data);
        Equal(expected, SarifConverter.Read(second.Root, "*.sarif", maximumKeys: null, maximumTextLength: null).Data);
    }

    private static void ResolvesPaths()
    {
        using var folder = new Folder();
        var rootUri = "file://" + (folder.Root.StartsWith('/') ? string.Empty : "/") + folder.Root.Replace('\\', '/');
        var outside = "file:///elsewhere/lib/util.py";
        var cases = new (string Location, string Path)[]
        {
            ("""{"uri":"src/a.py"}""", "src/a.py"),
            ("""{"uri":"./src//b.py"}""", "src/b.py"),
            ("""{"uri":"src/x/../c.py"}""", "src/c.py"),
            ("""{"uri":"src/with%20space.py"}""", "src/with space.py"),
            ("""{"uri":"src/a.py","uriBaseId":"%SRCROOT%"}""", "src/a.py"),
            ("""{"uri":"a.py","uriBaseId":"SRC"}""", "src/a.py"),
            ("""{"uri":"a.py","uriBaseId":"NESTED"}""", "src/inner/a.py"),
            ("""{"uri":"a.py","uriBaseId":"ROOT"}""", "a.py"),
            ($$$"""{"uri":"{{{rootUri}}}/pkg/d.py"}""", "pkg/d.py"),
            ($$$"""{"uri":"{{{rootUri.Replace("file://", "file://localhost", StringComparison.Ordinal)}}}/pkg/e.py"}""", "pkg/e.py"),
            ($$$"""{"uri":"{{{outside}}}"}""", $"<outside>/{Short(outside)}/util.py"),
            ("""{"uri":"../sibling/f.py"}""", $"<outside>/{Short("../sibling/f.py")}/f.py"),
            ("""{"uri":"https://example.com/g.py"}""", $"<outside>/{Short("https://example.com/g.py")}/g.py"),
            ("""{"uri":"file://server/share/h.py"}""", $"<outside>/{Short("file://server/share/h.py")}/h.py"),
            ("""{"index":0}""", "from/artifacts.py"),
        };
        var results = cases.Select((item, index) =>
            $$$"""{"ruleId":"R","message":{"text":"{{{index}}}"},"fingerprints":{"f":"{{{index}}}"},"locations":[{"physicalLocation":{"artifactLocation":{{{item.Location}}}}}]}""").ToList();
        results.Add("""{"ruleId":"R","message":{"text":"none"},"fingerprints":{"f":"none"}}""");
        results.Add("""{"ruleId":"R","message":{"text":"logical"},"fingerprints":{"f":"logical"},"locations":[{"logicalLocations":[{"name":"Main"}]}]}""");
        var run = $$$"""
            {"tool":{"driver":{"name":"T"}},
             "originalUriBaseIds":{"ROOT":{"uri":"{{{rootUri}}}/"},"SRC":{"uri":"src/","uriBaseId":"ROOT"},"NESTED":{"uri":"inner","uriBaseId":"SRC"}},
             "artifacts":[{"location":{"uri":"from/artifacts.py"}}],
             "results":[{{{string.Join(",", results)}}}]}
            """;
        folder.Write("paths.sarif", Log(run));
        var read = SarifConverter.Read(folder.Root, "paths.sarif", maximumKeys: null, maximumTextLength: null);
        Equal(string.Empty, string.Join(" | ", read.Problems));
        var paths = Findings(read.Data!).ToDictionary(finding => finding.GetProperty("message").GetString()!, finding => finding.GetProperty("path").GetString()!);
        for (var index = 0; index < cases.Length; index++)
        {
            Equal(cases[index].Path, paths[index.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        Equal("<none>", paths["none"]);
        Equal("<none>", paths["logical"]);

        folder.Write("slash.sarif", Log(Run("T", """{"ruleId":"R","message":{"text":"m"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"src\\a.py"}}}]}""")));
        Equal("'slash.sarif' run 0 result 0: the URI 'src\\a.py' has a backslash; a SARIF URI separates folders with '/'.",
            SarifConverter.Read(folder.Root, "slash.sarif", maximumKeys: null, maximumTextLength: null).Problems.Single());
    }

    // A message given by id is the rule's message string with its arguments in place.
    private static void ReadsMessageStrings()
    {
        const string given = """{"ruleId":"R","ruleIndex":0,"message":{"id":"default","arguments":["x","f"]}}""";
        const string missing = """{"ruleId":"R","message":{"id":"missing"}}""";
        const string rules = """[{"id":"R","messageStrings":{"default":{"text":"'{0}' is unused in {1}; {{literal}} {5}"}}}]""";
        using var folder = new Folder();
        folder.Write("m.sarif", Log(RunWithRules("T", rules, [given, missing])));
        var result = SarifConverter.Read(folder.Root, "m.sarif", maximumKeys: null, maximumTextLength: null);
        Equal("'m.sarif' run 0 result 1: the result's message has no text, and no message string of its rule that its id names.", result.Problems.Single());
        folder.Write("m.sarif", Log(RunWithRules("T", rules, [given])));
        Equal("'x' is unused in f; {literal} {5}", Findings(SarifConverter.Read(folder.Root, "m.sarif", maximumKeys: null, maximumTextLength: null).Data!).Single().GetProperty("message").GetString());
    }

    // Control characters in a message become spaces, and a message over maxTextLength is cut. The
    // fingerprint is taken from the message as written.
    private static void CleansAndCutsMessages()
    {
        using var folder = new Folder();
        folder.Write("m.sarif", Log(Run("T", """{"ruleId":"R","message":{"text":"first line\nsecond\tline"}}""")));
        var full = Findings(SarifConverter.Read(folder.Root, "m.sarif", maximumKeys: null, maximumTextLength: null).Data!).Single();
        Equal("first line second line", full.GetProperty("message").GetString());
        var cut = SarifConverter.Read(folder.Root, "m.sarif", maximumKeys: null, maximumTextLength: 6);
        Equal("first…", Findings(cut.Data!).Single().GetProperty("message").GetString());
        using var document = JsonDocument.Parse(cut.Data!);
        Equal($"T/R/<none>/{Digest("first line\nsecond\tline")}", document.RootElement.GetProperty("results").EnumerateObject().Single().Name);
    }

    private static void RejectsMalformedInput()
    {
        var cases = new (string Content, string Problem)[]
        {
            ("""{"version":"2.1.0","runs":[""", "'bad.sarif' is not valid JSON:"),
            ("""{"version":"2.1.0", /* note */ "runs":[]}""", "'bad.sarif' is not valid JSON:"),
            ("""{"version":"2.1.0","runs":[],}""", "'bad.sarif' is not valid JSON:"),
            ("""{"version":"2.1.0","version":"2.1.0","runs":[]}""", "'bad.sarif' is not valid JSON:"),
            ("""{"version":"2.0.0","runs":[]}""", "'bad.sarif': the file is SARIF version '2.0.0'; only SARIF 2.1.0 is read."),
            ("""{"runs":[]}""", "'bad.sarif': the file states no SARIF version; only SARIF 2.1.0 is read."),
            ("""[]""", "'bad.sarif': a SARIF log must be a JSON object."),
            ("""{"version":"2.1.0"}""", "'bad.sarif': a SARIF log must have a 'runs' array."),
            ("""{"version":"2.1.0","runs":null}""", "'bad.sarif': 'runs' is null, which SARIF uses when the tool could not run; no findings are recorded from it."),
            ("""{"version":"2.1.0","runs":[{"tool":{"driver":{}},"results":[]}]}""", "'bad.sarif' run 0: the run has no tool.driver.name, which names the tool."),
            ("""{"version":"2.1.0","runs":[{"tool":{"driver":{"name":"T"}},"results":{}}]}""", "'bad.sarif' run 0: 'results' must be an array."),
            ("""{"version":"2.1.0","runs":[{"tool":{"driver":{"name":"T"}},"results":[{"ruleId":"R"}]}]}""", "'bad.sarif' run 0 result 0: the result has no message."),
            ("""{"version":"2.1.0","runs":[{"tool":{"driver":{"name":"T"}},"results":[{"ruleId":"R","message":{"text":"m"},"locations":[{"physicalLocation":{"region":{"startLine":0}}}]}]}]}""",
                "'bad.sarif' run 0 result 0: the region's startLine must be a whole number of at least 1."),
            ("""{"version":"2.1.0","runs":[{"tool":{"driver":{"name":"T"}},"results":[{"ruleId":"R\u0001","message":{"text":"m"}}]}]}""",
                "'bad.sarif' run 0 result 0: the rule id must be valid Unicode with no control characters."),
        };
        foreach (var (content, problem) in cases)
        {
            using var folder = new Folder();
            folder.Write("bad.sarif", content);
            var result = SarifConverter.Read(folder.Root, "bad.sarif", maximumKeys: null, maximumTextLength: null);
            Equal(null, result.Data);
            Equal(1, result.Problems.Count);
            if (!result.Problems[0].StartsWith(problem, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Expected a problem starting '{problem}', got '{result.Problems[0]}'.");
            }

            // The rejected entry still states the file it read.
            Equal(Hex(Encoding.UTF8.GetBytes(content)), result.Sources.Single().Sha256);
        }

        // A byte order mark is allowed; the digest is of the bytes as they are.
        using var bom = new Folder();
        var bytes = (byte[])[0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Log(Run("T")))];
        File.WriteAllBytes(Path.Combine(bom.Root, "bom.sarif"), bytes);
        var withBom = SarifConverter.Read(bom.Root, "bom.sarif", maximumKeys: null, maximumTextLength: null);
        Equal(0, withBom.Problems.Count);
        Equal(Hex(bytes), withBom.Sources.Single().Sha256);

        // A glob that matches nothing is rejected as for any converter, and one broken file of
        // several rejects the entry while the others are still read.
        Equal("No file matches 'none/*.sarif'.", SarifConverter.Read(bom.Root, "none/*.sarif", maximumKeys: null, maximumTextLength: null).Problems.Single());
        bom.Write("zz.sarif", "{");
        var mixed = SarifConverter.Read(bom.Root, "*.sarif", maximumKeys: null, maximumTextLength: null);
        Equal(null, mixed.Data);
        Equal("bom.sarif,zz.sarif", string.Join(",", mixed.Sources.Select(source => source.Path)));
        Equal(true, mixed.Problems.Single().StartsWith("'zz.sarif' is not valid JSON:", StringComparison.Ordinal));
    }

    private static void RejectsNestingPastTheLimit()
    {
        // The log object is one level, so 255 arrays inside it reach the limit of 256.
        using var folder = new Folder();
        folder.Write("deep.sarif", Nested(255));
        Equal(0, SarifConverter.Read(folder.Root, "deep.sarif", maximumKeys: null, maximumTextLength: null).Problems.Count);
        folder.Write("deep.sarif", Nested(256));
        Equal("'deep.sarif' nests deeper than 256 levels, the most a file may; it was not read.",
            SarifConverter.Read(folder.Root, "deep.sarif", maximumKeys: null, maximumTextLength: null).Problems.Single());
        folder.Write("deep.sarif", Nested(5000));
        Equal("'deep.sarif' nests deeper than 256 levels, the most a file may; it was not read.",
            SarifConverter.Read(folder.Root, "deep.sarif", maximumKeys: null, maximumTextLength: null).Problems.Single());

        static string Nested(int arrays) =>
            "{\"version\":\"2.1.0\",\"runs\":[],\"properties\":{\"deep\":" + new string('[', arrays - 1) + new string(']', arrays - 1) + "}}";
    }

    private static void RejectsFilesPastTheByteLimits()
    {
        Equal(64L * 1024 * 1024, SarifConverter.MaximumFileBytes);
        Equal(256L * 1024 * 1024, SarifConverter.MaximumEntryBytes);
        using var folder = new Folder();
        var log = Log(Run("T"));
        folder.Write("a.sarif", log);
        folder.Write("b.sarif", log);
        var length = Encoding.UTF8.GetByteCount(log);
        Equal(0, SarifConverter.Read(folder.Root, "*.sarif", null, null, fileLimit: length, entryLimit: 2 * length).Problems.Count);

        var file = SarifConverter.Read(folder.Root, "*.sarif", null, null, fileLimit: length - 1, entryLimit: 2 * length);
        Equal(null, file.Data);
        Equal($"'a.sarif' is {length} bytes, more than the {length - 1} bytes one file may hold; it was not read.", file.Problems.Single());

        var entry = SarifConverter.Read(folder.Root, "*.sarif", null, null, fileLimit: length, entryLimit: (2 * length) - 1);
        Equal(null, entry.Data);
        Equal("a.sarif", entry.Sources.Single().Path);
        Equal($"The files '*.sarif' matches hold more than the {(2 * length) - 1} bytes one entry may read; 'b.sarif' and the files after it were not read.", entry.Problems.Single());
    }

    // maxKeys counts findings by key, after duplicates are kept once; more rejects the entry.
    private static void RejectsMoreFindingsThanTheBudget()
    {
        using var folder = new Folder();
        folder.Write("a.sarif", Log(Run("T", Result("R", "error", "a.py", 1, "x"), Result("R", "error", "a.py", 2, "x"), Result("R", "error", "b.py", 1, "x"))));
        Equal(0, SarifConverter.Read(folder.Root, "a.sarif", maximumKeys: 2, maximumTextLength: null).Problems.Count);
        var over = SarifConverter.Read(folder.Root, "a.sarif", maximumKeys: 1, maximumTextLength: null);
        Equal(null, over.Data);
        Equal(1, over.Sources.Count);
        Equal(true, over.Problems.Single().StartsWith("The files 'a.sarif' matches hold 2 findings by key, more than the root file's maxKeys budget of 1.", StringComparison.Ordinal));
    }

    // A synthetic log in the shape CodeQL writes: rules in a query pack extension, results with
    // ruleId, rule.index and rule.toolComponent, partialFingerprints and %SRCROOT% locations.
    private static void ReadsARealisticCodeQlLog()
    {
        const string log = """
            {
              "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
              "version": "2.1.0",
              "runs": [
                {
                  "tool": {
                    "driver": {
                      "name": "CodeQL",
                      "organization": "GitHub",
                      "semanticVersion": "2.19.0",
                      "notifications": [],
                      "rules": []
                    },
                    "extensions": [
                      {
                        "name": "codeql/javascript-queries",
                        "semanticVersion": "1.2.0",
                        "rules": [
                          {
                            "id": "js/unused-local-variable",
                            "name": "js/unused-local-variable",
                            "shortDescription": { "text": "Unused variable, import, function or class" },
                            "defaultConfiguration": { "enabled": true, "level": "note" },
                            "properties": { "tags": ["maintainability"], "precision": "very-high" }
                          },
                          {
                            "id": "js/sql-injection",
                            "name": "js/sql-injection",
                            "shortDescription": { "text": "Database query built from user-controlled sources" },
                            "defaultConfiguration": { "enabled": true, "level": "error" },
                            "properties": { "security-severity": "8.8" }
                          }
                        ]
                      }
                    ]
                  },
                  "invocations": [ { "executionSuccessful": true, "toolExecutionNotifications": [] } ],
                  "artifacts": [
                    { "location": { "uri": "src/server.js", "uriBaseId": "%SRCROOT%", "index": 0 } },
                    { "location": { "uri": "src/util.js", "uriBaseId": "%SRCROOT%", "index": 1 } }
                  ],
                  "results": [
                    {
                      "ruleId": "js/sql-injection",
                      "rule": { "id": "js/sql-injection", "index": 1, "toolComponent": { "index": 0 } },
                      "message": { "text": "This query string depends on a [user-provided value](1)." },
                      "locations": [
                        {
                          "physicalLocation": {
                            "artifactLocation": { "uri": "src/server.js", "uriBaseId": "%SRCROOT%", "index": 0 },
                            "region": { "startLine": 42, "startColumn": 18, "endColumn": 61 }
                          }
                        }
                      ],
                      "partialFingerprints": { "primaryLocationLineHash": "5f2b8a1c9d3e7f40:1", "primaryLocationStartColumnFingerprint": "13" },
                      "relatedLocations": [ { "id": 1, "physicalLocation": { "artifactLocation": { "uri": "src/server.js", "uriBaseId": "%SRCROOT%", "index": 0 }, "region": { "startLine": 40 } }, "message": { "text": "user-provided value" } } ]
                    },
                    {
                      "ruleId": "js/unused-local-variable",
                      "rule": { "id": "js/unused-local-variable", "index": 0, "toolComponent": { "index": 0 } },
                      "message": { "text": "Unused variable tmp." },
                      "locations": [
                        {
                          "physicalLocation": {
                            "artifactLocation": { "uri": "src/util.js", "uriBaseId": "%SRCROOT%", "index": 1 },
                            "region": { "startLine": 7, "startColumn": 9, "endColumn": 12 }
                          }
                        }
                      ],
                      "partialFingerprints": { "primaryLocationLineHash": "a07c33e1b2d4f561:1", "primaryLocationStartColumnFingerprint": "4" }
                    }
                  ],
                  "columnKind": "utf16CodeUnits",
                  "properties": { "semmle.formatSpecifier": "sarif-latest" }
                }
              ]
            }
            """;
        Equal(
            """{"byRule":{"CodeQL/js/sql-injection":{"value":"1"},"CodeQL/js/unused-local-variable":{"value":"1"}},"results":{"""
            + "\"CodeQL/js/sql-injection/src/server.js/5f2b8a1c9d3e7f40:1\":{\"value\":{\"level\":\"error\",\"line\":\"42\",\"message\":\"This query string depends on a [user-provided value](1).\",\"path\":\"src/server.js\",\"rule\":\"js/sql-injection\",\"tool\":\"CodeQL\"}},"
            + "\"CodeQL/js/unused-local-variable/src/util.js/a07c33e1b2d4f561:1\":{\"value\":{\"level\":\"note\",\"line\":\"7\",\"message\":\"Unused variable tmp.\",\"path\":\"src/util.js\",\"rule\":\"js/unused-local-variable\",\"tool\":\"CodeQL\"}}},\"total\":{\"value\":\"2\"}}",
            Data(log));
    }

    // A synthetic log in the shape Ruff writes with --output-format sarif: absolute file URIs, a
    // level on every result and no fingerprints.
    private static void ReadsARealisticRuffLog()
    {
        using var folder = new Folder();
        var rootUri = "file://" + (folder.Root.StartsWith('/') ? string.Empty : "/") + folder.Root.Replace('\\', '/');
        var log = $$$"""
            {
              "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
              "runs": [
                {
                  "results": [
                    {
                      "level": "error",
                      "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "{{{rootUri}}}/app/main.py" }, "region": { "endColumn": 10, "endLine": 1, "startColumn": 8, "startLine": 1 } } } ],
                      "message": { "text": "`os` imported but unused" },
                      "ruleId": "F401"
                    },
                    {
                      "level": "error",
                      "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "{{{rootUri}}}/app/main.py" }, "region": { "endColumn": 89, "endLine": 12, "startColumn": 89, "startLine": 12 } } } ],
                      "message": { "text": "Line too long (93 > 88)" },
                      "ruleId": "E501"
                    },
                    {
                      "level": "error",
                      "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "{{{rootUri}}}/app/main.py" }, "region": { "endColumn": 89, "endLine": 30, "startColumn": 89, "startLine": 30 } } } ],
                      "message": { "text": "Line too long (101 > 88)" },
                      "ruleId": "E501"
                    }
                  ],
                  "tool": {
                    "driver": {
                      "informationUri": "https://github.com/astral-sh/ruff",
                      "name": "ruff",
                      "rules": [
                        { "fullDescription": { "text": "Checks for unused imports." }, "help": { "text": "`{name}` imported but unused" }, "id": "F401", "properties": { "id": "F401", "kind": "Pyflakes", "name": "unused-import" }, "shortDescription": { "text": "`{name}` imported but unused" } },
                        { "fullDescription": { "text": "Checks for lines that exceed the specified maximum character length." }, "help": { "text": "Line too long ({width} > {limit})" }, "id": "E501", "properties": { "id": "E501", "kind": "pycodestyle", "name": "line-too-long" }, "shortDescription": { "text": "Line too long ({width} > {limit})" } }
                      ],
                      "version": "0.8.0"
                    }
                  }
                }
              ],
              "version": "2.1.0"
            }
            """;
        folder.Write("ruff.sarif", log);
        var result = SarifConverter.Read(folder.Root, "ruff.sarif", maximumKeys: null, maximumTextLength: null);
        Equal(string.Empty, string.Join(" | ", result.Problems));

        // The two long lines differ only in digits, so without fingerprints they are one finding
        // that occurs twice.
        Equal(
            """{"byRule":{"ruff/E501":{"value":"2"},"ruff/F401":{"value":"1"}},"results":{"""
            + $$$"""
            "ruff/E501/app/main.py/{{{Digest("Line too long (93 > 88)")}}}":{"value":{"level":"error","line":"12","message":"Line too long (93 > 88)","occurrences":"2","path":"app/main.py","rule":"E501","tool":"ruff"}},
            """.TrimEnd()
            + $$$$"""
            "ruff/F401/app/main.py/{{{{Digest("`os` imported but unused")}}}}":{"value":{"level":"error","line":"1","message":"`os` imported but unused","path":"app/main.py","rule":"F401","tool":"ruff"}}},"total":{"value":"3"}}
            """.TrimEnd(),
            result.Data);
    }

    private static string Log(params string[] runs) => $$$"""{"version":"2.1.0","runs":[{{{string.Join(",", runs)}}}]}""";

    private static string Run(string tool, params string[] results) =>
        $$$"""{"tool":{"driver":{"name":"{{{tool}}}"}},"results":[{{{string.Join(",", results)}}}]}""";

    private static string RunWithRules(string tool, string rules, string[] results) =>
        $$$"""{"tool":{"driver":{"name":"{{{tool}}}","rules":{{{rules}}}}},"results":[{{{string.Join(",", results)}}}]}""";

    private static string Result(string rule, string level, string path, int line, string message) =>
        $$$$"""{"ruleId":"{{{{rule}}}}","level":"{{{{level}}}}","message":{"text":"{{{{message}}}}"},"locations":[{"physicalLocation":{"artifactLocation":{"uri":"{{{{path}}}}"},"region":{"startLine":{{{{line}}}}}}}]}""";

    // The fingerprint of a result with none: the SHA-256 of its message with digits removed.
    private static string Digest(string message) =>
        Hex(Encoding.UTF8.GetBytes(new string([.. message.Where(character => !char.IsAsciiDigit(character))])));

    // The digest part of a path outside the repository.
    private static string Short(string written) => Hex(Encoding.UTF8.GetBytes(written))[..12];

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Data(string log)
    {
        using var folder = new Folder();
        folder.Write("log.sarif", log);
        var result = SarifConverter.Read(folder.Root, "log.sarif", maximumKeys: null, maximumTextLength: null);
        if (result.Problems.Count > 0)
        {
            throw new InvalidOperationException($"The log was rejected: {string.Join(" | ", result.Problems)}");
        }

        return result.Data!;
    }

    private static string Keys(string log)
    {
        using var document = JsonDocument.Parse(Data(log));
        return string.Join(",", document.RootElement.GetProperty("results").EnumerateObject().Select(property => property.Name));
    }

    // Each finding's message and level, in the order the results were given.
    private static string Levels(string log) =>
        string.Join(",", Findings(Data(log))
            .Select(finding => (Message: finding.GetProperty("message").GetString()!, Level: finding.GetProperty("level").GetString()!))
            .OrderBy(pair => Order(pair.Message))
            .Select(pair => $"{pair.Message}={pair.Level}"));

    private static int Order(string message) => Array.IndexOf(
        ["given", "default", "fail kind", "pass kind", "informational kind", "pass with level", "no default", "no rule metadata"], message);

    private static List<JsonElement> Findings(string data)
    {
        using var document = JsonDocument.Parse(data);
        return [.. document.RootElement.GetProperty("results").EnumerateObject().Select(property => property.Value.GetProperty("value").Clone())];
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

            Root = Path.Combine(repository?.FullName ?? Path.GetTempPath(), "temp", $"sarif-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Write(string path, string content)
        {
            var full = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
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
