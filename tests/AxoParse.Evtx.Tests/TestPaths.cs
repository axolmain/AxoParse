namespace AxoParse.Evtx.Tests;

/// <summary>
/// Shared path constants for test data files.
/// </summary>
internal static class TestPaths
{
    #region Non-Public Fields

    /// <summary>
    /// Absolute path to the test data directory containing .evtx sample files.
    /// </summary>
    internal static readonly string TestDataDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data"));

    /// <summary>
    /// Absolute path to this test project's source directory (bin/&lt;config&gt;/&lt;tfm&gt; is three levels below it).
    /// Used for files a test reads and, when re-blessing, rewrites in the repository.
    /// </summary>
    internal static readonly string ProjectDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));

    #endregion
}