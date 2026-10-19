namespace AxoParse.Evtx;

/// <summary>
/// What an <see cref="EvtxDiagnostic"/> reports.
/// </summary>
public enum EvtxDiagnosticKind
{
    /// <summary>
    /// A chunk was skipped because its CRC32 header or data checksum didn't match
    /// (<see cref="EvtxOptions.ValidateChecksums"/> only). Has no record ID.
    /// </summary>
    ChunkChecksumMismatch,

    /// <summary>
    /// A record's BinXml couldn't be decoded; the event has empty output.
    /// </summary>
    RecordRenderFailed,

    /// <summary>
    /// A record's BinXml is corrupt and its output ends where decoding stopped (<see cref="EvtxOptions.Salvage"/> only).
    /// </summary>
    RecordSalvaged,
}

/// <summary>
/// A problem found while parsing: a skipped chunk or a record that didn't decode cleanly. Parsing continues past it.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="RecordId">Event record ID of the affected record, or null for a chunk-level diagnostic.</param>
/// <param name="FileOffset">Absolute file offset of the affected record (its header) or chunk.</param>
/// <param name="Message">Human-readable description, including the decoder's reason for record diagnostics.</param>
public readonly record struct EvtxDiagnostic(
    EvtxDiagnosticKind Kind,
    ulong? RecordId,
    long FileOffset,
    string Message)
{
    #region Public Methods

    /// <summary>
    /// Returns <see cref="Message"/>.
    /// </summary>
    /// <returns>The message.</returns>
    public override string ToString() => Message;

    #endregion
}
