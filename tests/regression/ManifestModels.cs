namespace Diffra.Regression;

public sealed record RegressionManifest(
    string Version,
    IReadOnlyList<RegressionCase> Cases);

public sealed record RegressionCase(
    string Id,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyList<string> InputFiles,
    IReadOnlyList<ExpectedOutput> ExpectedOutputs,
    int ExpectedExitCode);

public sealed record ExpectedOutput(
    string Path,
    string OutputArg,
    bool NormalizeEnvironment = false,
    bool IsDirectory = false);
