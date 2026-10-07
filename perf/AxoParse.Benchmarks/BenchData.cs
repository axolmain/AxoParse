namespace AxoParse.Benchmarks;

/// <summary>
/// Locates and loads EVTX sample files from <c>tests/data</c> for benchmarks.
/// BenchmarkDotNet runs each benchmark from a generated project under <c>bin/</c>, so the data
/// directory is found by walking up from the executable until <c>tests/data</c> exists.
/// </summary>
internal static class BenchData
{
    #region Public Methods

    /// <summary>
    /// Reads a sample file relative to <c>tests/data</c>.
    /// </summary>
    /// <param name="relativePath">Path under <c>tests/data</c>, e.g. <c>security.evtx</c> or <c>benchmark/security_big_sample.evtx</c>.</param>
    /// <returns>The complete file bytes.</returns>
    public static byte[] Load(string relativePath) => File.ReadAllBytes(Path.Combine(DataDir, relativePath));

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Absolute path to <c>tests/data</c>, resolved once.
    /// </summary>
    private static readonly string DataDir = FindDataDir();

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Walks up from the executable directory looking for <c>tests/data</c>.
    /// </summary>
    /// <returns>Absolute path of the data directory.</returns>
    private static string FindDataDir()
    {
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "data");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find tests/data above {AppContext.BaseDirectory}. Run benchmarks from inside the repository.");
    }

    #endregion
}
