using System.Collections.Concurrent;
using AxoParse.Evtx.BinXml;

namespace AxoParse.Evtx.Wevt;

/// <summary>
/// Template cache built from WEVT_TEMPLATE resources in Windows provider PE binaries, compiled into
/// <see cref="CompiledTemplate"/> objects.
/// <para>
/// Not yet used during parsing: <see cref="Evtx.EvtxParser"/> copies these templates into a dictionary its chunk workers
/// never read (they use per-thread caches), so passing a cache has no effect on output. The intended use, matching the
/// Rust evtx crate, is a fallback for chunks whose embedded templates are missing or corrupt.
/// </para>
/// </summary>
public sealed class WevtCache
{
    #region Properties

    /// <summary>
    /// Number of templates that were successfully compiled into <see cref="CompiledTemplate"/> objects.
    /// </summary>
    /// <remarks>
    /// O(n) — iterates the entire dictionary. Intended for diagnostics, not hot-path use.
    /// </remarks>
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
    /// Total number of template GUIDs in the cache (including those that failed compilation).
    /// </summary>
    public int Count => _templates.Count;

    #endregion

    #region Public Methods

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

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Copies the WEVT templates into <paramref name="targetCache"/> with
    /// <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/>. The parser's chunk workers don't read that dictionary, so
    /// this currently has no effect on output. Seeding the worker caches instead would be wrong: the cache is checked
    /// before a chunk's own template is compiled, so WEVT templates would override in-file ones, and
    /// <see cref="WevtTemplateCompiler"/> handles optional attributes differently from the EVTX template compiler.
    /// </summary>
    /// <param name="targetCache">The parser's compiled template cache to pre-populate.</param>
    internal void PopulateCache(ConcurrentDictionary<Guid, CompiledTemplate?> targetCache)
    {
        foreach (KeyValuePair<Guid, CompiledTemplate?> kvp in _templates)
        {
            targetCache.TryAdd(kvp.Key, kvp.Value);
        }
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Compiled templates keyed by GUID. First-in wins for duplicate GUIDs.
    /// Null values indicate templates that were extracted but failed compilation.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, CompiledTemplate?> _templates = new();

    #endregion
}