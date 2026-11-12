using Diffra.RootFile;

namespace Diffra.Protocol.Tests;

/// <summary>
/// Checks the root file loader: the worked examples load, the YAML subset is enforced, and every
/// name a project or dataset refers to must be declared. Shape checks that only the schema makes
/// (rule bodies, for example) are not repeated here.
/// </summary>
public static class RootFileTests
{
    private const string Header = "schema: https://diffra.net/schemas/root-file-v1.schema.json\n";

    private const string Warnings = """
        datasets:
          warnings:
            type: count
            unit: warnings
            from: { payload: "metrics.json" }
            rules:
              default:
                check: { fail: { above: 0 } }

        """;

    public static void RunAll()
    {
        LoadsTheExamples();
        ReadsTheReleaseTrainExample();
        ReadsTheCustomExample();
        RejectsStructurallyInvalidFixtures();
        RejectsAnUndeclaredDataset();
        RejectsAnUndeclaredRuleSet();
        RejectsARuleSetOnADatasetWithoutRules();
        TreatsOnlyAPlainNullAsNoRules();
        RejectsAnUndeclaredCustomType();
        RejectsAnUndeclaredDimension();
        ReadsWhatRecordingNeeds();
        RejectsBadDimensionsAndSources();
        RejectsTheWrongSchema();
        RejectsUnsupportedYaml();
        RejectsBadSettings();
        RejectsOversizedInput();
        ReportsEveryProblemWithItsPosition();
        Console.WriteLine("PASS root file loading and cross-references");
    }

    private static void LoadsTheExamples()
    {
        var directory = Path.Combine(FindRepositoryRoot(), "examples", "root-file");
        var files = Directory.GetFiles(directory, "*.yml");
        if (files.Length < 2)
        {
            throw new InvalidOperationException($"Expected the root file examples in {directory}.");
        }

        foreach (var file in files)
        {
            LoadsCleanly(RootFileLoader.LoadFile(file), Path.GetFileName(file));
        }
    }

    private static void ReadsTheReleaseTrainExample()
    {
        var document = LoadExample("release-train.yml");
        Equal(4, document.Datasets.Count);
        Equal(false, document.Datasets["windowsRegression"].Required);
        Equal(true, document.Datasets["tests"].Required);
        Equal("diffra/test-results@1", document.Datasets["tests"].TypeName);
        Equal("main,release", string.Join(",", document.Datasets["tests"].RuleSets));
        Equal("release", document.Projects["release"].Datasets["endToEnd"]);
        Equal(20, document.Settings.HistoryCommits);
        Equal(25, document.Settings.OutputLines);
        Equal(500, document.Settings.OutputAgentTokens);
        Equal(null, document.Settings.MaxKeys);
    }

    private static void ReadsTheCustomExample()
    {
        var document = LoadExample("custom.yml");
        var core = document.Projects["core"];
        Equal(true, core.Datasets.ContainsKey("runtimeVersion"));
        Equal(null, core.Datasets["runtimeVersion"]);
        Equal("default", core.Datasets["analyzers"]);
        Equal("fixtureCheck", document.Datasets["schemaFixtures"].TypeName);
        Equal(0, document.Datasets["runtimeVersion"].RuleSets.Count);
        Equal(500, document.Settings.MaxKeys);
    }

    // Some schema-invalid fixtures are also caught by the loader, because it reads those parts.
    // The rest (rule shapes, for example) are caught only by the schema.
    private static void RejectsStructurallyInvalidFixtures()
    {
        var directory = Path.Combine(FindRepositoryRoot(), "examples", "root-file", "invalid");
        Fails(RootFileLoader.LoadFile(Path.Combine(directory, "no-projects.yml")), "at least one project");
        Fails(RootFileLoader.LoadFile(Path.Combine(directory, "project-without-rule-set-name.yml")), "rule-set name or null");
        Fails(RootFileLoader.LoadFile(Path.Combine(directory, "scope-is-gone.yml")), "Unknown top-level key 'scope'");
    }

    private static void RejectsAnUndeclaredDataset() =>
        Fails(
            RootFileLoader.Load(Header + Warnings + "projects:\n  main: { datasets: { warnings: default, coverage: default } }\n"),
            "selects dataset 'coverage', which is not declared");

    private static void RejectsAnUndeclaredRuleSet() =>
        Fails(
            RootFileLoader.Load(Header + Warnings + "projects:\n  main: { datasets: { warnings: strict } }\n"),
            "applies rule set 'strict' to dataset 'warnings', but it defines default");

    private static void RejectsARuleSetOnADatasetWithoutRules()
    {
        const string text = """
            datasets:
              version:
                type: text
                from: { payload: "metrics.json" }
            projects:
              main: { datasets: { version: default } }
            """;
        Fails(RootFileLoader.Load(Header + text), "it defines no rule sets");
        LoadsCleanly(RootFileLoader.Load(Header + text.Replace("version: default", "version: null", StringComparison.Ordinal)), "null mapping");
        LoadsCleanly(RootFileLoader.Load(Header + text.Replace("version: default", "version: ~", StringComparison.Ordinal)), "~ mapping");
        LoadsCleanly(RootFileLoader.Load(Header + text.Replace("{ version: default }", "{ version: }", StringComparison.Ordinal)), "empty mapping");
    }

    // A quoted "null" is the name of a rule set, which the name pattern allows; only a plain null means no rules.
    private static void TreatsOnlyAPlainNullAsNoRules()
    {
        Fails(
            RootFileLoader.Load(Header + Warnings + "projects:\n  main: { datasets: { warnings: \"null\" } }\n"),
            "applies rule set 'null' to dataset 'warnings'");

        var named = Header + Warnings.Replace("      default:", "      \"null\":", StringComparison.Ordinal)
            + "projects:\n  main: { datasets: { warnings: 'null' } }\n";
        var document = LoadsCleanly(RootFileLoader.Load(named), "rule set named null");
        Equal("null", document.Projects["main"].Datasets["warnings"]);
    }

    private static void RejectsAnUndeclaredCustomType()
    {
        const string text = """
            datasets:
              check:
                type: fixtureCheck
                from: { payload: "fixtures.json" }
            projects:
              main: { datasets: { check: null } }
            """;
        Fails(RootFileLoader.Load(Header + text), "type 'fixtureCheck', which is neither a built-in type nor declared under 'types'");

        const string types = """
            types:
              fixtureCheck:
                variant:
                  accepted: {}
                  rejected: { message: text }

            """;
        LoadsCleanly(RootFileLoader.Load(Header + types + text), "declared custom type");
        Fails(
            RootFileLoader.Load(Header + text.Replace("fixtureCheck", "diffra/unknown@1", StringComparison.Ordinal)),
            "not a standard dataset type");
    }

    private static void RejectsAnUndeclaredDimension()
    {
        const string text = """
            datasets:
              tests:
                type: diffra/test-results@1
                from: { junit: "tests/*.xml" }
                fields:
                  results: { stratify: [os] }
              duration:
                type: duration
                stratify: [runner]
                from: { payload: "metrics.json" }
            projects:
              main: { datasets: { tests: null, duration: null } }
            """;
        var result = RootFileLoader.Load(Header + text);
        Fails(result, "stratified by 'os', which is not declared under 'dimensions'");
        Fails(result, "stratified by 'runner', which is not declared under 'dimensions'");

        const string dimensions = "dimensions:\n  os: { values: [linux, windows] }\n  runner: { observed: true }\n";
        LoadsCleanly(RootFileLoader.Load(Header + dimensions + text), "declared dimensions");
    }

    private static void ReadsWhatRecordingNeeds()
    {
        const string text = """
            dimensions:
              os: { values: [linux, windows] }
              runner: { observed: true }
            datasets:
              tests:
                type: diffra/test-results@1
                from: { junit: "tests/*.xml" }
                fields:
                  results: { stratify: [os, runner] }
                  duration: { stratify: [os] }
              suites:
                type: duration
                unit: s
                collection: { kind: map, keys: { normalization: none } }
                stratify: [runner]
                from: { payload: "metrics.json" }
            projects:
              main: { datasets: { tests: null, suites: null } }
            """;
        var document = LoadsCleanly(RootFileLoader.Load(Header + text), "recording example");
        Equal("linux,windows", string.Join(",", document.Dimensions["os"].Values!));
        Equal(true, document.Dimensions["runner"].Values is null);
        var tests = document.Datasets["tests"];
        Equal("os,runner", string.Join(",", tests.Stratify));
        Equal("tests/*.xml", tests.From["junit"]);
        Equal(false, tests.Keyed);
        Equal(true, tests.Unit is null);
        var suites = document.Datasets["suites"];
        Equal(true, suites.Keyed);
        Equal("s", suites.Unit);
        Equal("runner", string.Join(",", suites.Stratify));
        Equal("metrics.json", suites.From["payload"]);
    }

    private static void RejectsBadDimensionsAndSources()
    {
        const string text = """
            dimensions:
              os: { values: [linux, linux] }
              arch: [x64]
            datasets:
              size:
                type: bytes
                unit: " B"
              time:
                type: duration
                from: { payload: }
            projects:
              main: { datasets: { size: null, time: null } }
            """;
        var result = RootFileLoader.Load(Header + text);
        Fails(result, "Dimension 'os' lists a value more than once.");
        Fails(result, "Dimension 'arch' must be");
        Fails(result, "Dataset 'size' must name where it is read from under 'from'.");
        Fails(result, "Dataset 'size' has a 'unit' that is not text");
        Fails(result, "Dataset 'time' reads 'payload' from something that is not a glob.");
    }

    private static void RejectsTheWrongSchema()
    {
        var body = Warnings + "projects:\n  main: { datasets: { warnings: default } }\n";
        Fails(RootFileLoader.Load(body), "must name its schema");
        Fails(RootFileLoader.Load("schema: https://diffra.dev/schemas/root-file-v1.schema.json\n" + body), "The schema must be");
    }

    private static void RejectsUnsupportedYaml()
    {
        var body = Warnings + "projects:\n  main: { datasets: { warnings: default } }\n";
        Fails(RootFileLoader.Load(string.Empty), "empty");
        Fails(RootFileLoader.Load(Header + body + "---\n" + Header), "more than one YAML document");
        Fails(RootFileLoader.Load(Header + "settings: &s { history: { commits: 3 } }\n" + body), "Anchors are not allowed");
        Fails(RootFileLoader.Load(Header + body + "projects2: *s\n"), "Aliases are not allowed");
        Fails(RootFileLoader.Load(Header + "settings: !!map { history: { commits: 3 } }\n" + body), "Tags are not allowed");
        Fails(RootFileLoader.Load(Header + Header + body), "appears twice");
        Fails(RootFileLoader.Load(Header + "? [a, b]\n: c\n" + body), "must be text");
        Fails(RootFileLoader.Load(Header + "datasets: [\n"), string.Empty);
        Fails(RootFileLoader.Load(Header + "settings: !<> x\n" + body), "not valid YAML");
        Fails(RootFileLoader.Load(Header + "settings: !!x+y x\n" + body), "not valid YAML");
        Fails(RootFileLoader.Load(Header + Warnings + "projects:\n  main: { datasets: { warnings: ! null } }\n"), "Tags are not allowed");
        Fails(RootFileLoader.Load(Header + Warnings + "projects:\n  main: { datasets: { warnings: true } }\n"), "quote it");
        Fails(RootFileLoader.Load(Header + Warnings + "projects:\n  \"main\\n\": { datasets: { warnings: default } }\n"), "not a valid name");
        Fails(RootFileLoader.Load("- just\n- a list\n"), "must be a mapping");

        var deep = Header + "settings:\n" + string.Concat(Enumerable.Range(1, 80).Select(i => new string(' ', i * 2) + "k" + i + ":\n"));
        Fails(RootFileLoader.Load(deep + body), "nests deeper than");
    }

    private static void RejectsBadSettings()
    {
        var body = Warnings + "projects:\n  main: { datasets: { warnings: default } }\n";
        Fails(RootFileLoader.Load(Header + "settings: { history: { commits: 1 } }\n" + body), "'commits' must be a whole number of at least 2");
        Fails(RootFileLoader.Load(Header + "settings: { output: { lines: \"25\" } }\n" + body), "'lines' must be a whole number");
        Fails(RootFileLoader.Load(Header + "settings: { output: { width: 80 } }\n" + body), "Unknown setting 'output.width'");
        Fails(RootFileLoader.Load(Header + "settings: { colour: true }\n" + body), "Unknown setting 'colour'");
        Fails(RootFileLoader.Load(Header + Warnings.Replace("unit: warnings", "unit: warnings\n    required: maybe", StringComparison.Ordinal)
            + "projects:\n  main: { datasets: { warnings: default } }\n"), "'required' that is not true or false");
    }

    private static void RejectsOversizedInput()
    {
        var text = Header + "# " + new string('x', RootFileLoader.MaxBytes) + "\n";
        Fails(RootFileLoader.Load(text), "the limit is");
    }

    private static void ReportsEveryProblemWithItsPosition()
    {
        const string text = """
            schema: https://diffra.net/schemas/root-file-v1.schema.json
            datasets:
              warnings:
                type: count
                unit: warnings
                from: { payload: "metrics.json" }
            projects:
              main:
                datasets:
                  warnings: strict
                  missing: null
            """;
        var result = RootFileLoader.Load(text);
        Equal(false, result.Succeeded);
        Equal(2, result.Problems.Count);
        var ruleSet = result.Problems.Single(p => p.Message.Contains("'strict'", StringComparison.Ordinal));
        Equal(10L, ruleSet.Line);
        Equal(17L, ruleSet.Column);
        var dataset = result.Problems.Single(p => p.Message.Contains("'missing'", StringComparison.Ordinal));
        Equal(11L, dataset.Line);
        Equal(7L, dataset.Column);
        Equal("10:17: " + ruleSet.Message, ruleSet.ToString());
    }

    private static RootFileDocument LoadExample(string name) =>
        LoadsCleanly(RootFileLoader.LoadFile(Path.Combine(FindRepositoryRoot(), "examples", "root-file", name)), name);

    private static RootFileDocument LoadsCleanly(RootFileLoadResult result, string what)
    {
        if (!result.Succeeded || result.Problems.Count != 0 || result.Document is null)
        {
            throw new InvalidOperationException($"{what} should load, but: {string.Join("; ", result.Problems)}");
        }

        return result.Document;
    }

    private static void Fails(RootFileLoadResult result, string expected)
    {
        if (result.Succeeded || result.Problems.Count == 0)
        {
            throw new InvalidOperationException($"Expected a problem containing '{expected}', but the file loaded.");
        }

        if (!result.Problems.Any(p => p.Message.Contains(expected, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Expected a problem containing '{expected}', got: {string.Join("; ", result.Problems)}");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
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
}
