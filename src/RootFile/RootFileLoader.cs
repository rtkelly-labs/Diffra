using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Diffra.RootFile;

/// <summary>
/// Loads the root file (docs/adr/0002-project-file-and-datasets.md) and checks what
/// <c>schemas/root-file-v1.schema.json</c> cannot: that every name a project or dataset refers
/// to is declared. It reads the parts that recording needs (datasets, rule-set names, projects and
/// settings) and checks their shape. It does not validate the file against the schema, so a rule's
/// shape is checked only by the schema.
/// </summary>
public static partial class RootFileLoader
{
    public const string SchemaId = "https://diffra.net/schemas/root-file-v1.schema.json";

    /// <summary>The largest root file accepted, in UTF-8 bytes. A safety limit, not a setting.</summary>
    public const int MaxBytes = 1024 * 1024;

    private static readonly HashSet<string> TopLevelKeys =
        new(["schema", "settings", "dimensions", "types", "datasets", "projects"], StringComparer.Ordinal);

    private static readonly HashSet<string> ScalarTypes =
        new(["count", "integer", "decimal", "bytes", "duration", "ratio", "datetime", "enum", "digest", "text"], StringComparer.Ordinal);

    private static readonly HashSet<string> StandardTypes =
        new(["diffra/benchmark@1", "diffra/coverage@1", "diffra/findings@1", "diffra/size@1", "diffra/test-results@1"], StringComparer.Ordinal);

    private static readonly string[] OutputKeys = ["lines", "findings", "columns", "agentTokens"];
    private static readonly string[] HistoryKeys = ["commits"];
    private static readonly string[] LimitKeys = ["maxKeys", "maxStrata", "maxTextLength"];

    [GeneratedRegex("^[a-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public static RootFileLoadResult LoadFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var length = new FileInfo(path).Length;
        if (length > MaxBytes)
        {
            return Failed(new RootFileProblem(1, 1, $"The root file is {length} bytes; the limit is {MaxBytes}."));
        }

        return Load(File.ReadAllText(path, Encoding.UTF8));
    }

    public static RootFileLoadResult Load(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var length = Encoding.UTF8.GetByteCount(text);
        if (length > MaxBytes)
        {
            return Failed(new RootFileProblem(1, 1, $"The root file is {length} bytes; the limit is {MaxBytes}."));
        }

        var problems = new List<RootFileProblem>();
        var root = YamlTree.Parse(text, problems);
        if (root is null)
        {
            return new RootFileLoadResult(null, problems);
        }

        var document = Read(root, problems);
        return problems.Count == 0 ? new RootFileLoadResult(document, []) : new RootFileLoadResult(null, problems);
    }

    private static RootFileLoadResult Failed(RootFileProblem problem) => new(null, [problem]);

    private static RootFileDocument? Read(YamlNode root, List<RootFileProblem> problems)
    {
        if (root is not YamlMapping file)
        {
            problems.Add(At(root, "The root file must be a mapping with schema, datasets and projects."));
            return null;
        }

        foreach (var entry in file.Entries)
        {
            if (!TopLevelKeys.Contains(entry.Key.Value))
            {
                problems.Add(At(entry.Key, $"Unknown top-level key '{entry.Key.Value}'."));
            }
        }

        var schema = file.Get("schema");
        if (schema is null)
        {
            problems.Add(At(file, $"The root file must name its schema: 'schema: {SchemaId}'."));
        }
        else if (schema is not YamlScalar { IsNull: false } schemaText || !string.Equals(schemaText.Value, SchemaId, StringComparison.Ordinal))
        {
            problems.Add(At(schema, $"The schema must be '{SchemaId}'."));
        }

        var settings = ReadSettings(file.Get("settings"), problems);
        var dimensions = ReadNames(file.Get("dimensions"), "dimensions", problems);
        var types = ReadNames(file.Get("types"), "types", problems);
        var datasets = ReadDatasets(file, dimensions, types, problems);
        var projects = ReadProjects(file, datasets, problems);
        return new RootFileDocument(settings, datasets, projects);
    }

    private static HashSet<string> ReadNames(YamlNode? node, string section, List<RootFileProblem> problems)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (node is null)
        {
            return names;
        }

        if (node is not YamlMapping mapping)
        {
            problems.Add(At(node, $"'{section}' must be a mapping of names."));
            return names;
        }

        foreach (var entry in mapping.Entries)
        {
            CheckName(entry.Key, problems);
            names.Add(entry.Key.Value);
        }

        return names;
    }

    private static Dictionary<string, RootDataset> ReadDatasets(
        YamlMapping file,
        HashSet<string> dimensions,
        HashSet<string> types,
        List<RootFileProblem> problems)
    {
        var datasets = new Dictionary<string, RootDataset>(StringComparer.Ordinal);
        var node = file.Get("datasets");
        if (node is not YamlMapping { Entries.Count: > 0 } mapping)
        {
            problems.Add(At(node ?? file, "The root file must declare at least one dataset under 'datasets'."));
            return datasets;
        }

        foreach (var (key, value) in mapping.Entries)
        {
            var name = key.Value;
            CheckName(key, problems);
            if (value is not YamlMapping dataset)
            {
                problems.Add(At(value, $"Dataset '{name}' must be a mapping."));
                continue;
            }

            var typeName = ReadType(name, dataset, types, problems);
            var required = ReadRequired(name, dataset, problems);
            var ruleSets = ReadRuleSetNames(name, dataset, problems);
            CheckStratify(name, dataset.Get("stratify"), dimensions, problems);
            if (dataset.Get("fields") is YamlMapping fields)
            {
                foreach (var (_, field) in fields.Entries)
                {
                    if (field is YamlMapping fieldMapping)
                    {
                        CheckStratify(name, fieldMapping.Get("stratify"), dimensions, problems);
                    }
                }
            }

            datasets[name] = new RootDataset(name, typeName, required, ruleSets);
        }

        return datasets;
    }

    private static string ReadType(string name, YamlMapping dataset, HashSet<string> types, List<RootFileProblem> problems)
    {
        var node = dataset.Get("type");
        if (node is not YamlScalar { IsNull: false } type)
        {
            problems.Add(At(node ?? dataset, $"Dataset '{name}' must have a 'type'."));
            return string.Empty;
        }

        var value = type.Value;
        if (value.StartsWith("diffra/", StringComparison.Ordinal))
        {
            if (!StandardTypes.Contains(value))
            {
                problems.Add(At(type, $"Dataset '{name}' has type '{value}', which is not a standard dataset type."));
            }
        }
        else if (!ScalarTypes.Contains(value) && !types.Contains(value))
        {
            problems.Add(At(type, $"Dataset '{name}' has type '{value}', which is neither a built-in type nor declared under 'types'."));
        }

        return value;
    }

    private static bool ReadRequired(string name, YamlMapping dataset, List<RootFileProblem> problems)
    {
        var node = dataset.Get("required");
        if (node is null)
        {
            return true;
        }

        if (node is YamlScalar { Plain: true } flag)
        {
            if (flag.Value is "true" or "True" or "TRUE")
            {
                return true;
            }

            if (flag.Value is "false" or "False" or "FALSE")
            {
                return false;
            }
        }

        problems.Add(At(node, $"Dataset '{name}' has 'required' that is not true or false."));
        return true;
    }

    private static List<string> ReadRuleSetNames(string name, YamlMapping dataset, List<RootFileProblem> problems)
    {
        var names = new List<string>();
        var node = dataset.Get("rules");
        if (node is null)
        {
            return names;
        }

        if (node is not YamlMapping rules)
        {
            problems.Add(At(node, $"Dataset '{name}' has 'rules' that is not a mapping of rule-set names."));
            return names;
        }

        foreach (var entry in rules.Entries)
        {
            CheckName(entry.Key, problems);
            names.Add(entry.Key.Value);
        }

        return names;
    }

    private static void CheckStratify(string name, YamlNode? node, HashSet<string> dimensions, List<RootFileProblem> problems)
    {
        if (node is null)
        {
            return;
        }

        if (node is not YamlSequence sequence)
        {
            problems.Add(At(node, $"Dataset '{name}' has 'stratify' that is not a list of dimensions."));
            return;
        }

        foreach (var item in sequence.Items)
        {
            if (item is not YamlScalar { IsNull: false } dimension)
            {
                problems.Add(At(item, $"Dataset '{name}' has a 'stratify' entry that is not a dimension name."));
            }
            else if (!dimensions.Contains(dimension.Value))
            {
                problems.Add(At(item, $"Dataset '{name}' is stratified by '{dimension.Value}', which is not declared under 'dimensions'."));
            }
        }
    }

    private static Dictionary<string, RootProject> ReadProjects(
        YamlMapping file,
        Dictionary<string, RootDataset> datasets,
        List<RootFileProblem> problems)
    {
        var projects = new Dictionary<string, RootProject>(StringComparer.Ordinal);
        var node = file.Get("projects");
        if (node is not YamlMapping { Entries.Count: > 0 } mapping)
        {
            problems.Add(At(node ?? file, "The root file must declare at least one project under 'projects'."));
            return projects;
        }

        foreach (var (key, value) in mapping.Entries)
        {
            var name = key.Value;
            CheckName(key, problems);
            if (value is not YamlMapping project)
            {
                problems.Add(At(value, $"Project '{name}' must be a mapping."));
                continue;
            }

            foreach (var entry in project.Entries)
            {
                if (entry.Key.Value is not ("description" or "datasets"))
                {
                    problems.Add(At(entry.Key, $"Project '{name}' has an unknown key '{entry.Key.Value}'."));
                }
            }

            var selected = new Dictionary<string, string?>(StringComparer.Ordinal);
            var selection = project.Get("datasets");
            if (selection is not YamlMapping { Entries.Count: > 0 } selectionMapping)
            {
                problems.Add(At(selection ?? project, $"Project '{name}' must map each dataset it selects to a rule-set name or null."));
                projects[name] = new RootProject(name, selected);
                continue;
            }

            foreach (var (datasetKey, ruleSetNode) in selectionMapping.Entries)
            {
                var datasetName = datasetKey.Value;
                var ruleSet = ReadRuleSetReference(name, datasetName, ruleSetNode, problems);
                selected[datasetName] = ruleSet;
                if (!datasets.TryGetValue(datasetName, out var dataset))
                {
                    problems.Add(At(datasetKey, $"Project '{name}' selects dataset '{datasetName}', which is not declared under 'datasets'."));
                }
                else if (ruleSet is not null && !dataset.RuleSets.Contains(ruleSet, StringComparer.Ordinal))
                {
                    var defined = dataset.RuleSets.Count == 0 ? "it defines no rule sets" : $"it defines {string.Join(", ", dataset.RuleSets)}";
                    problems.Add(At(ruleSetNode, $"Project '{name}' applies rule set '{ruleSet}' to dataset '{datasetName}', but {defined}."));
                }
            }

            projects[name] = new RootProject(name, selected);
        }

        return projects;
    }

    private static string? ReadRuleSetReference(string project, string dataset, YamlNode node, List<RootFileProblem> problems)
    {
        if (node is not YamlScalar scalar)
        {
            problems.Add(At(node, $"Project '{project}' must map dataset '{dataset}' to a rule-set name or null."));
            return null;
        }

        if (scalar.IsNull)
        {
            return null;
        }

        CheckName(scalar, problems);
        return scalar.Value;
    }

    private static RootSettings ReadSettings(YamlNode? node, List<RootFileProblem> problems)
    {
        if (node is null)
        {
            return new RootSettings(null, null, null, null, null, null, null, null);
        }

        if (node is not YamlMapping settings)
        {
            problems.Add(At(node, "'settings' must be a mapping."));
            return new RootSettings(null, null, null, null, null, null, null, null);
        }

        var output = Section(settings, "output", OutputKeys, problems);
        var history = Section(settings, "history", HistoryKeys, problems);
        var limits = Section(settings, "limits", LimitKeys, problems);
        foreach (var entry in settings.Entries)
        {
            if (entry.Key.Value is not ("output" or "history" or "limits"))
            {
                problems.Add(At(entry.Key, $"Unknown setting '{entry.Key.Value}'."));
            }
        }

        return new RootSettings(
            Integer(output, "lines", 1, problems),
            Integer(output, "findings", 1, problems),
            Integer(output, "columns", 1, problems),
            Integer(output, "agentTokens", 1, problems),
            Integer(history, "commits", 2, problems),
            Integer(limits, "maxKeys", 1, problems),
            Integer(limits, "maxStrata", 1, problems),
            Integer(limits, "maxTextLength", 1, problems));
    }

    private static YamlMapping? Section(YamlMapping settings, string name, string[] keys, List<RootFileProblem> problems)
    {
        var node = settings.Get(name);
        if (node is null)
        {
            return null;
        }

        if (node is not YamlMapping section)
        {
            problems.Add(At(node, $"Setting '{name}' must be a mapping."));
            return null;
        }

        foreach (var entry in section.Entries)
        {
            if (Array.IndexOf(keys, entry.Key.Value) < 0)
            {
                problems.Add(At(entry.Key, $"Unknown setting '{name}.{entry.Key.Value}'."));
            }
        }

        return section;
    }

    private static int? Integer(YamlMapping? section, string key, int minimum, List<RootFileProblem> problems)
    {
        var node = section?.Get(key);
        if (node is null)
        {
            return null;
        }

        if (node is YamlScalar { Plain: true } scalar
            && int.TryParse(scalar.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value >= minimum)
        {
            return value;
        }

        problems.Add(At(node, $"Setting '{key}' must be a whole number of at least {minimum}."));
        return null;
    }

    private static void CheckName(YamlScalar key, List<RootFileProblem> problems)
    {
        if (!NamePattern().IsMatch(key.Value))
        {
            problems.Add(At(key, $"'{key.Value}' is not a valid name: start with a lowercase letter, then letters and digits."));
        }
    }

    private static RootFileProblem At(YamlNode node, string message) => new(node.Line, node.Column, message);
}
