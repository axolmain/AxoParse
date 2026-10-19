namespace AxoParse.Evtx;

/// <summary>
/// Typed fields an <see cref="EvtxParser"/> operation reads for each event (<see cref="EvtxOptions.Fields"/>).
/// </summary>
[Flags]
public enum EvtxFields
{
    /// <summary>
    /// No typed fields; only the rendered output.
    /// </summary>
    None = 0,

    /// <summary>
    /// The <c>System</c> fields: <see cref="EvtxEvent.EventId"/>, <see cref="EvtxEvent.Provider"/>,
    /// <see cref="EvtxEvent.Level"/>, <see cref="EvtxEvent.TimeCreated"/>, <see cref="EvtxEvent.Channel"/>, ….
    /// </summary>
    System = 0x1,

    /// <summary>
    /// The EventData/UserData name/value pairs (<see cref="EvtxEvent.EventData"/>), which cost a formatted value per
    /// data item. Implies <see cref="System"/>.
    /// </summary>
    EventData = 0x2,

    /// <summary>
    /// Every typed field.
    /// </summary>
    All = System | EventData,
}

/// <summary>
/// Settings for every <see cref="EvtxParser"/> operation. Immutable: create one with an object initializer
/// (<c>new EvtxOptions { Format = OutputFormat.JsonStructured, MaxThreads = 4 }</c>) or derive one with <c>with</c>, and share it
/// freely across threads and calls.
/// </summary>
public sealed record EvtxOptions
{
    #region Properties

    /// <summary>
    /// The defaults: XML output, all cores, no checksum validation, strict decoding, no provider templates.
    /// </summary>
    public static EvtxOptions Default { get; } = new();

    /// <summary>
    /// Output format each event is rendered in. Defaults to <see cref="OutputFormat.Xml"/>.
    /// </summary>
    public OutputFormat Format { get; init; } = OutputFormat.Xml;

    /// <summary>
    /// Worker threads that parse chunks in parallel: 0 (the default) or a negative value uses every core,
    /// 1 parses on the calling thread. Output and event order never depend on it.
    /// </summary>
    public int MaxThreads { get; init; }

    /// <summary>
    /// When true, chunks whose CRC32 header or data checksum doesn't match are skipped, with an
    /// <see cref="EvtxDiagnosticKind.ChunkChecksumMismatch"/> diagnostic. Off by default: chunks of a log that was
    /// still being written (or was copied while open) often carry stale checksums but valid records.
    /// </summary>
    public bool ValidateChecksums { get; init; }

    /// <summary>
    /// When true, a record whose BinXml is corrupt keeps the well-formed output decoded before the corruption (open
    /// elements are closed) instead of failing with empty output; its <see cref="EvtxEvent.Diagnostic"/> says where
    /// decoding stopped. Off by default: corrupt records fail, as in the Rust evtx crate.
    /// </summary>
    public bool Salvage { get; init; }

    /// <summary>
    /// Provider (WEVT) templates, used only for records whose embedded template is missing, runs past its chunk or
    /// won't compile; a usable embedded template is never replaced. Null (the default) for none.
    /// </summary>
    public WevtCache? WevtCache { get; init; }

    /// <summary>
    /// Typed fields to read for each event (<see cref="EvtxEvent.EventId"/>, <see cref="EvtxEvent.Provider"/>, …
    /// and <see cref="EvtxEvent.EventData"/>). None by default, so parses that only need the rendered output don't pay
    /// for them; reading a field that wasn't requested throws. Not used by <c>WriteTo</c>.
    /// </summary>
    public EvtxFields Fields { get; init; }

    /// <summary>
    /// When true, events are not rendered: <see cref="EvtxEvent.Utf8"/> is empty and only the typed fields are read
    /// (System fields always, plus EventData when <see cref="Fields"/> asks for it), which is much faster for counting
    /// and statistics. <see cref="Format"/> is ignored, and a record's diagnostic only reports a failure to read its
    /// fields. Not allowed with <c>WriteTo</c>.
    /// </summary>
    public bool FieldsOnly { get; init; }

    /// <summary>
    /// <see cref="EvtxParser.WriteTo(string, Stream, EvtxOptions?, CancellationToken)"/> with XML output only: wrap the
    /// records in an <c>&lt;Events&gt;</c> root element (on lines of their own) so the output is a single well-formed
    /// XML document. Off by default: the output is one record after another, each followed by a line feed. Ignored for
    /// JSON formats.
    /// </summary>
    public bool XmlEventsRoot { get; init; }

    #endregion

    #region Non-Public Properties

    /// <summary>
    /// Number of worker threads <see cref="MaxThreads"/> resolves to.
    /// </summary>
    internal int Workers => MaxThreads > 0 ? MaxThreads : Environment.ProcessorCount;

    #endregion
}
