namespace Diffra.RootFile;

/// <summary>A problem found while loading the root file, at a 1-based line and column.</summary>
public sealed record RootFileProblem(long Line, long Column, string Message)
{
    public override string ToString() => $"{Line}:{Column}: {Message}";
}

/// <summary>
/// One dataset: a collected signal with its type. <see cref="RuleSets"/> are the names of the rule
/// sets it defines; the rules themselves are not interpreted by the loader.
/// </summary>
public sealed record RootDataset(string Name, string TypeName, bool Required, IReadOnlyList<string> RuleSets);

/// <summary>
/// One project: the datasets it selects, each mapped to the rule set it applies, or to
/// <see langword="null"/> to record and show the dataset without applying rules.
/// </summary>
public sealed record RootProject(string Name, IReadOnlyDictionary<string, string?> Datasets);

/// <summary>The settings the file states. A setting the file leaves out is <see langword="null"/>.</summary>
public sealed record RootSettings(
    int? OutputLines,
    int? OutputFindings,
    int? OutputColumns,
    int? OutputAgentTokens,
    int? HistoryCommits,
    int? MaxKeys,
    int? MaxStrata,
    int? MaxTextLength);

/// <summary>A loaded root file whose cross-references all resolve.</summary>
public sealed record RootFileDocument(
    RootSettings Settings,
    IReadOnlyDictionary<string, RootDataset> Datasets,
    IReadOnlyDictionary<string, RootProject> Projects);

/// <summary>
/// The result of loading a root file: the document, or every problem found. Loading never throws
/// on bad input, so all problems can be reported together.
/// </summary>
public sealed record RootFileLoadResult(RootFileDocument? Document, IReadOnlyList<RootFileProblem> Problems)
{
    public bool Succeeded => Document is not null;
}
