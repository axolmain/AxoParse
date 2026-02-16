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

    #endregion
}