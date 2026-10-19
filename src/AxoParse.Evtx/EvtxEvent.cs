using System.Text;

namespace AxoParse.Evtx;

/// <summary>
/// One event record: its header fields, its typed <c>System</c> fields, its rendered output and any decoding
/// diagnostic. A small view over the <see cref="EvtxChunk"/> that holds the data, so copying it is cheap and reading it
/// allocates nothing (except <see cref="GetText"/> and EventData values). Immutable and safe to read from any thread.
/// <para>
/// The typed fields (<see cref="EventId"/>, <see cref="Provider"/>, …) are read only when requested with
/// <see cref="EvtxOptions.Fields"/> (or <see cref="EvtxOptions.FieldsOnly"/>); reading one otherwise throws
/// <see cref="InvalidOperationException"/>. They come from the event template's substitution values as the record is
/// parsed, not from the rendered text, and are the same in every output format. A field the event doesn't have keeps
/// its default (0, or null).
/// </para>
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
    /// <c>System/Correlation@ActivityID</c>, or null.
    /// </summary>
    public Guid? ActivityId =>
        (System.Present & SystemPresence.ActivityId) != 0 ? System.ActivityId : null;

    /// <summary>
    /// <c>System/Channel</c> (e.g. <c>Security</c>), or null.
    /// </summary>
    public string? Channel => System.Channel;

    /// <summary>
    /// <c>System/Computer</c>, or null.
    /// </summary>
    public string? Computer => System.Computer;

    /// <summary>
    /// The event's EventData or UserData name/value pairs. Read only with <see cref="EvtxFields.EventData"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The log was parsed without <see cref="EvtxFields.EventData"/>.</exception>
    public EvtxEventData EventData
    {
        get
        {
            if (_chunk.Fields?.PairEnds is not { } ends)
                throw new InvalidOperationException("EventData was not read: parse with EvtxOptions.Fields = EvtxFields.EventData.");
            return new EvtxEventData(_chunk.Fields, _index == 0 ? 0 : ends[_index - 1], ends[_index]);
        }
    }

    /// <summary>
    /// <c>System/EventID</c>.
    /// </summary>
    public ushort EventId => System.EventId;

    /// <summary>
    /// <c>System/Keywords</c>.
    /// </summary>
    public ulong Keywords => System.Keywords;

    /// <summary>
    /// <c>System/Level</c> (1 critical, 2 error, 3 warning, 4 information, 5 verbose; 0 for log-always events).
    /// </summary>
    public byte Level => System.Level;

    /// <summary>
    /// <c>System/Opcode</c>.
    /// </summary>
    public byte Opcode => System.Opcode;

    /// <summary>
    /// <c>System/Execution@ProcessID</c>.
    /// </summary>
    public uint ProcessId => System.ProcessId;

    /// <summary>
    /// <c>System/Provider@Name</c> (e.g. <c>Microsoft-Windows-Security-Auditing</c>), or null.
    /// </summary>
    public string? Provider => System.Provider;

    /// <summary>
    /// <c>System/Provider@Guid</c>, or null.
    /// </summary>
    public Guid? ProviderGuid =>
        (System.Present & SystemPresence.ProviderGuid) != 0 ? System.ProviderGuid : null;

    /// <summary>
    /// <c>System/Task</c>.
    /// </summary>
    public ushort Task => System.Task;

    /// <summary>
    /// <c>System/Execution@ThreadID</c>.
    /// </summary>
    public uint ThreadId => System.ThreadId;

    /// <summary>
    /// <c>System/TimeCreated@SystemTime</c> in UTC; <see cref="WrittenTimeUtc"/> when the event has none.
    /// </summary>
    public DateTime TimeCreated =>
        (System.Present & SystemPresence.TimeCreated) != 0
            ? new DateTime(System.TimeCreatedTicks, DateTimeKind.Utc)
            : WrittenTimeUtc;

    /// <summary>
    /// <c>System/Security@UserID</c> in SDDL form (e.g. <c>S-1-5-18</c>), or null.
    /// </summary>
    public string? UserId => System.UserId;

    /// <summary>
    /// <c>System/Version</c>.
    /// </summary>
    public byte Version => System.Version;

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

    #region Non-Public Properties

    /// <summary>
    /// The record's System fields.
    /// </summary>
    private ref readonly SystemFields System => ref _chunk.SystemFieldsOf(_index);

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
