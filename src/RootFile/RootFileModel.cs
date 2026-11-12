namespace Diffra.RootFile;

/// <summary>A problem found while loading the root file, at a 1-based line and column.</summary>
public sealed record RootFileProblem(long Line, long Column, string Message)
{
    public override string ToString() => $"{Line}:{Column}: {Message}";
}

/// <summary>
/// One dataset: a collected signal with its type. <see cref="RuleSets"/> are the names of the rule
/// sets it defines; the rules themselves are not interpreted by the loader. <see cref="From"/> maps
/// each converter named in <c>from</c> to its glob. <see cref="Keyed"/> is true for a
/// <c>map</c> collection, whose values are given one key at a time. <see cref="Stratify"/> is every
/// dimension the dataset or one of its fields is stratified by, in the order first named.
/// </summary>
public sealed record RootDataset(
    string Name,
    string TypeName,
    bool Required,
    IReadOnlyList<string> RuleSets,
    string? Unit,
    IReadOnlyDictionary<string, string> From,
    bool Keyed,
    IReadOnlyList<string> Stratify);

/// <summary>
/// One dimension. <see cref="Values"/> lists the values the root file plans for, or is
/// <see langword="null"/> for an observed dimension, whose values are whatever the jobs report.
/// </summary>
public sealed record RootDimension(string Name, IReadOnlyList<string>? Values);

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
    IReadOnlyDictionary<string, RootDimension> Dimensions,
    IReadOnlyDictionary<string, RootDataset> Datasets,
    IReadOnlyDictionary<string, RootProject> Projects);

/// <summary>
/// The result of loading a root file: the document, or every problem found. Loading never throws
/// on bad text, so all problems can be reported together. <c>LoadFile</c> still throws when the
/// file cannot be read.
/// </summary>
public sealed record RootFileLoadResult(RootFileDocument? Document, IReadOnlyList<RootFileProblem> Problems)
{
    public bool Succeeded => Document is not null;
}
