using System.Collections.Concurrent;
using AxoParse.Evtx.BinXml;
using AxoParse.Evtx.Wevt;

namespace AxoParse.Evtx;

/// <summary>
/// Template cache built from WEVT_TEMPLATE resources in Windows provider PE binaries (DLL/EXE), keyed by template GUID.
/// <para>
/// Used as a <b>fallback</b>, as in the Rust evtx crate: when a record's template instance points at an embedded
/// template that is missing (size 0), runs past its chunk, or can't be compiled (corrupt), the parser renders the
/// record with the provider template of the same GUID instead of failing it. A valid embedded template is always used
/// as is, so passing a cache never changes output for intact logs. Typical use is carved or damaged logs.
/// </para>
/// </summary>
public sealed class WevtCache
{
    #region Properties

    /// <summary>
    /// Number of templates that compiled, i.e. that can be used as a fallback.
    /// </summary>
    /// <remarks>
    /// O(n) — iterates the entire dictionary. Intended for diagnostics, not hot-path use.
    /// </remarks>
    public int CompiledCount
    {
        get
        {
            int count = 0;
            foreach (KeyValuePair<Guid, WevtTemplateEntry> kvp in _templates)
            {
                if (kvp.Value.Program is not null)
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
            if (AddTemplate(wt.Guid, wt.BinXmlData))
                added++;
        }

        return added;
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Compiles one WEVT template body (a BinXml fragment with inline names) and adds it under
    /// <paramref name="guid"/>. First-in wins for duplicate GUIDs.
    /// </summary>
    /// <param name="guid">Template GUID, matching the GUID in EVTX template definitions.</param>
    /// <param name="binXml">Template body starting at the fragment header (TEMP entry offset 40).</param>
    /// <returns>True if the GUID was new.</returns>
    internal bool AddTemplate(Guid guid, ReadOnlySpan<byte> binXml) =>
        _templates.TryAdd(guid, new WevtTemplateEntry(CompileWevt(binXml)));

    /// <summary>
    /// The fallback template for <paramref name="guid"/> compiled for <paramref name="format"/>, or null if there is
    /// none, it didn't compile, or it can't be split into static parts (WEVT bodies use inline names, so the decoder
    /// can't render them directly).
    /// </summary>
    /// <param name="guid">Template GUID from the record's template instance.</param>
    /// <param name="format">Output format.</param>
    /// <returns>The compiled template, or null.</returns>
    internal CompiledTemplate? Get(Guid guid, OutputFormat format)
    {
        if (!_templates.TryGetValue(guid, out WevtTemplateEntry? entry))
            return null;
        CompiledTemplate? compiled = format == OutputFormat.JsonStructured ? entry.Json : entry.Xml;
        return compiled is { DecodesBody: true } ? null : compiled;
    }

    /// <summary>
    /// Compiles a WEVT template body with the shared template compiler, reading WEVT inline names. Malformed bodies
    /// (including ones that would read past the data) compile to null.
    /// </summary>
    /// <param name="binXml">Template body.</param>
    /// <returns>The program, or null.</returns>
    private static TemplateProgram? CompileWevt(ReadOnlySpan<byte> binXml)
    {
        if (binXml.Length < 4)
            return null;
        try
        {
            return TemplateProgramCompiler.Compile(binXml, default(WevtInlineNameReader));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Templates keyed by GUID. First-in wins for duplicate GUIDs.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, WevtTemplateEntry> _templates = new();

    #endregion

    #region Nested Types

    /// <summary>
    /// One provider template: its program (null if it didn't compile) and its XML/JSON compiled forms, built on first
    /// use. They are published without locking: racing threads build identical results and either may win.
    /// </summary>
    /// <param name="program">Compiled program, or null.</param>
    private sealed class WevtTemplateEntry(TemplateProgram? program)
    {
        /// <summary>
        /// Compiled program, or null if the template didn't compile.
        /// </summary>
        public TemplateProgram? Program { get; } = program;

        /// <summary>
        /// Compiled for XML, or null if <see cref="Program"/> is null.
        /// </summary>
        public CompiledTemplate? Xml => (_xml ??= Program is { } program ? BinXmlParser.CompileXml(program) : null);

        /// <summary>
        /// Compiled for JSON, or null if <see cref="Program"/> is null.
        /// </summary>
        public CompiledTemplate? Json => (_json ??= Program is { } program ? BinXmlParser.CompileJson(program) : null);

        /// <summary>
        /// Cached XML form.
        /// </summary>
        private CompiledTemplate? _xml;

        /// <summary>
        /// Cached JSON form.
        /// </summary>
        private CompiledTemplate? _json;
    }

    #endregion
}