namespace AxoParse.Evtx;

/// <summary>
/// A fully parsed EVTX file, returned by <see cref="EvtxParser.Parse(byte[], EvtxOptions?, CancellationToken)"/>.
/// Immutable and safe to read from any thread.
/// </summary>
public sealed class EvtxLog
{
    #region Constructors And Destructors

    /// <summary>
    /// Creates a parsed log.
    /// </summary>
    /// <param name="header">Parsed file header.</param>
    /// <param name="chunks">Chunks in output order: valid chunks in file order, then recovered regions.</param>
    /// <param name="recordCount">Total records across <paramref name="chunks"/>.</param>
    /// <param name="diagnostics">Every diagnostic, in output order.</param>
    internal EvtxLog(EvtxFileHeader header, EvtxChunk[] chunks, int recordCount, EvtxDiagnostic[] diagnostics)
    {
        Header = header;
        Chunks = chunks;
        RecordCount = recordCount;
        Diagnostics = diagnostics;
    }

    #endregion

    #region Properties

    /// <summary>
    /// Every chunk that produced events: valid chunks in file order, then regions whose chunk header was damaged and
    /// whose records were recovered (<see cref="EvtxChunk.Recovered"/>). Chunks skipped for a checksum mismatch are
    /// not included.
    /// </summary>
    public IReadOnlyList<EvtxChunk> Chunks { get; }

    /// <summary>
    /// Every diagnostic: chunks skipped for a checksum mismatch and records that didn't decode cleanly, in the order
    /// they were found (chunk by chunk, as <see cref="Chunks"/>).
    /// </summary>
    public IReadOnlyList<EvtxDiagnostic> Diagnostics { get; }

    /// <summary>
    /// All events, chunk by chunk in <see cref="Chunks"/> order.
    /// </summary>
    public IEnumerable<EvtxEvent> Events
    {
        get
        {
            foreach (EvtxChunk chunk in Chunks)
            {
                for (int i = 0; i < chunk.Count; i++)
                    yield return new EvtxEvent(chunk, i);
            }
        }
    }

    /// <summary>
    /// The file header (first 128 bytes): format version, chunk count and flags.
    /// </summary>
    public EvtxFileHeader Header { get; }

    /// <summary>
    /// Total number of events across all <see cref="Chunks"/>.
    /// </summary>
    public int RecordCount { get; }

    #endregion
}
