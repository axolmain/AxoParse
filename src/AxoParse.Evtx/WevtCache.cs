using System.Collections.Concurrent;

namespace AxoParse.Evtx;

/// <summary>
/// Offline template cache built from WEVT_TEMPLATE resources in Windows provider PE binaries.
/// Pre-compiles templates into <see cref="CompiledTemplate"/> objects so they can be injected
/// into the parser's compiled cache before chunk processing begins — zero hot-path overhead.
/// </summary>
public sealed class WevtCache
{
    /// <summary>
    /// Compiled templates keyed by GUID. First-in wins for duplicate GUIDs.
    /// Null values indicate templates that were extracted but failed compilation.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, CompiledTemplate?> _templates = new();

    /// <summary>
    /// Total number of template GUIDs in the cache (including those that failed compilation).
    /// </summary>
    public int Count => _templates.Count;

    /// <summary>
    /// Number of templates that were successfully compiled into <see cref="CompiledTemplate"/> objects.
    /// </summary>
    public int CompiledCount
    {
        get
        {
            int count = 0;
            foreach (KeyValuePair<Guid, CompiledTemplate?> kvp in _templates)
            {
                if (kvp.Value is not null)
                    count++;
            }
            return count;
        }
    }

    /// <summary>
    /// Extracts WEVT templates from raw PE file bytes, compiles them, and adds to the cache.
    /// First-in wins for duplicate GUIDs — subsequent calls with the same GUID are ignored.
    /// </summary>
    /// <param name="peData">Complete PE file bytes.</param>
    /// <returns>Number of new templates added to the cache.</returns>
    public int AddFromPeData(byte[] peData)
    {
        byte[]? wevtData = PeResourceReader.ExtractWevtTemplate(peData);
        if (wevtData is null)
            return 0;

        List<WevtTemplate> wevtTemplates = WevtManifest.ParseCrimManifest(wevtData);
        int added = 0;

        for (int i = 0; i < wevtTemplates.Count; i++)
        {
            WevtTemplate wt = wevtTemplates[i];
            CompiledTemplate? compiled = WevtTemplateCompiler.Compile(wt.BinXmlData);
            if (_templates.TryAdd(wt.Guid, compiled))
                added++;
        }

        return added;
    }

    /// <summary>
    /// Reads a PE file from disk and adds its WEVT templates to the cache.
    /// </summary>
    /// <param name="filePath">Path to a PE binary (DLL or EXE).</param>
    /// <returns>Number of new templates added to the cache.</returns>
    public int AddFromFile(string filePath)
    {
        byte[] peData = File.ReadAllBytes(filePath);
        return AddFromPeData(peData);
    }

    /// <summary>
    /// Scans a directory for PE files matching a glob pattern and adds all WEVT templates found.
    /// </summary>
    /// <param name="directory">Directory to scan.</param>
    /// <param name="pattern">File glob pattern (default: "*.dll").</param>
    /// <returns>Total number of new templates added across all matching files.</returns>
    public int AddFromDirectory(string directory, string pattern = "*.dll")
    {
        int total = 0;
        foreach (string file in Directory.EnumerateFiles(directory, pattern))
        {
            try
            {
                total += AddFromFile(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
            {
                // Skip files that can't be read or aren't valid PE binaries
            }
        }
        return total;
    }

    /// <summary>
    /// Pre-populates a parser's compiled template cache with WEVT templates.
    /// Uses <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/> so EVTX-embedded templates
    /// that are added later via <c>GetOrAdd</c> during chunk parsing will find the WEVT entry
    /// already present and skip the factory. Both should produce identical output for the same GUID.
    /// </summary>
    /// <param name="targetCache">The parser's compiled template cache to pre-populate.</param>
    internal void PopulateCache(ConcurrentDictionary<Guid, CompiledTemplate?> targetCache)
    {
        foreach (KeyValuePair<Guid, CompiledTemplate?> kvp in _templates)
        {
            targetCache.TryAdd(kvp.Key, kvp.Value);
        }
    }
}