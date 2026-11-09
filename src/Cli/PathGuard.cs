namespace Diffra.Cli;

/// <summary>Compares file paths the way the host file system resolves them.</summary>
internal static class PathGuard
{
    /// <summary>
    /// Windows and macOS file systems are case-insensitive by default, so paths that differ only
    /// in case name the same file there.
    /// </summary>
    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>Returns whether two paths resolve to the same full path on this host.</summary>
    public static bool SameFile(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), Comparison);
}
