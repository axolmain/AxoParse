using System.Text;

namespace AxoParse.Evtx;

/// <summary>
/// One event record: its header fields, its rendered output and any decoding diagnostic. A small view over the
/// <see cref="EvtxChunk"/> that holds the data, so copying it is cheap and reading it allocates nothing (except
/// <see cref="GetText"/>). Immutable and safe to read from any thread.
/// </summary>
public readonly struct EvtxEvent
{
    #region Constructors And Destructors

    /// <summary>
    /// Creates a view of record <paramref name="index"/> of <paramref name="chunk"/>.
    /// </summary>
    /// <param name="chunk">Chunk that holds the record.</param>
    /// <param name="index">Zero-based index of the record within the chunk.</param>
    internal EvtxEvent(EvtxChunk chunk, int index)
    {
        _chunk = chunk;
        _index = index;
    }

    #endregion

    #region Properties

    /// <summary>
    /// Decoding problem for this record, or null if it rendered cleanly.
    /// </summary>
    public EvtxDiagnostic? Diagnostic => _chunk.GetDiagnostic(_index);

    /// <summary>
    /// Absolute file offset of the record (its 24-byte header, which starts with the <c>0x00002A2A</c> magic).
    /// </summary>
    public long FileOffset => _chunk.RecordFileOffset(_index);

    /// <summary>
    /// True if the record rendered without a diagnostic.
    /// </summary>
    public bool IsSuccess => _chunk.GetDiagnostic(_index) is null;

    /// <summary>
    /// True if the record is corrupt and <see cref="Utf8"/> holds only the output decoded before the corruption
    /// (<see cref="EvtxOptions.Salvage"/>).
    /// </summary>
    public bool IsSalvaged => _chunk.GetDiagnostic(_index)?.Kind == EvtxDiagnosticKind.RecordSalvaged;

    /// <summary>
    /// Event record ID from the record header (the same value as the rendered <c>EventRecordID</c>).
    /// </summary>
    public ulong RecordId => _chunk.Record(_index).EventRecordId;

    /// <summary>
    /// The rendered event as UTF-8, in the <see cref="EvtxOptions.Format"/> it was parsed with. Empty when rendering
    /// failed. A slice of the chunk's output buffer, valid as long as the event is.
    /// </summary>
    public ReadOnlyMemory<byte> Utf8 => _chunk.Output(_index);

    /// <summary>
    /// When the record was written to the log (record header FILETIME), in UTC. <see cref="DateTime.MinValue"/> if the
    /// stored value is outside the range <see cref="DateTime"/> can represent (a corrupt record).
    /// </summary>
    public DateTime WrittenTimeUtc
    {
        get
        {
            ulong fileTime = _chunk.Record(_index).WrittenTime;
            return fileTime <= _maxFileTime ? DateTime.FromFileTimeUtc((long)fileTime) : DateTime.MinValue;
        }
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Decodes <see cref="Utf8"/> into a new string on every call.
    /// </summary>
    /// <returns>The rendered event text.</returns>
    public string GetText() => Encoding.UTF8.GetString(_chunk.Output(_index).Span);

    /// <summary>
    /// Returns <see cref="GetText"/>.
    /// </summary>
    /// <returns>The rendered event text.</returns>
    public override string ToString() => GetText();

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Largest FILETIME <see cref="DateTime.FromFileTimeUtc"/> accepts (9999-12-31T23:59:59.9999999Z).
    /// </summary>
    private const ulong _maxFileTime = 2_650_467_743_999_999_999UL;

    /// <summary>
    /// Chunk that holds the record.
    /// </summary>
    private readonly EvtxChunk _chunk;

    /// <summary>
    /// Index of the record within <see cref="_chunk"/>.
    /// </summary>
    private readonly int _index;

    #endregion
}
