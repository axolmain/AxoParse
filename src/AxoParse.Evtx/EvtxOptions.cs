namespace AxoParse.Evtx;

/// <summary>
/// Settings for every <see cref="EvtxParser"/> operation. Immutable: create one with an object initializer
/// (<c>new EvtxOptions { Format = OutputFormat.Json, MaxThreads = 4 }</c>) or derive one with <c>with</c>, and share it
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
    /// <see cref="EvtxParser.WriteTo(string, Stream, EvtxOptions?, CancellationToken)"/> with XML output only: wrap the
    /// records in an <c>&lt;Events&gt;</c> root element so the output is a single well-formed XML document. Off by
    /// default: the output is one record after another, each followed by a line feed.
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
