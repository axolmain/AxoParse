using System.Runtime.CompilerServices;

namespace AxoParse.Evtx;

/// <summary>
/// Which events an <see cref="EvtxParser"/> operation produces (<see cref="EvtxOptions.Filter"/>). An event must pass
/// every criterion that is set; unset (null) criteria accept everything. Events that don't pass are dropped before they
/// are rendered, so filtering saves the rendering cost, and a record-ID range skips whole chunks whose header shows
/// none of their records can match. Immutable: build one with an object initializer or derive one with <c>with</c>.
/// The collections are read when the filter is first used and must not change afterwards.
/// <para>
/// Every criterion except the record-ID range is checked against the event's typed <c>System</c> fields, which are
/// then read for every record (see <see cref="EvtxOptions.Fields"/>; reading them is much cheaper than rendering). A
/// corrupt record is checked against the fields read before the corruption, the same fields its event reports.
/// </para>
/// </summary>
public sealed record EvtxFilter
{
    #region Properties

    /// <summary>
    /// Event IDs to keep (<c>System/EventID</c>), or null for any.
    /// </summary>
    public IReadOnlyCollection<ushort>? EventIds { get; init; }

    /// <summary>
    /// Earliest <see cref="EvtxEvent.TimeCreated"/> to keep (inclusive), or null for no lower bound. Compared in UTC; a
    /// <see cref="DateTimeKind.Local"/> value is converted, an unspecified one is taken as UTC.
    /// </summary>
    public DateTime? From { get; init; }

    /// <summary>
    /// First record ID to keep (inclusive), or null for no lower bound. To resume reading after the last event you
    /// handled, set this to its <see cref="EvtxEvent.RecordId"/> + 1.
    /// </summary>
    public ulong? FromRecordId { get; init; }

    /// <summary>
    /// Levels to keep (<c>System/Level</c>: 1 critical, 2 error, 3 warning, 4 information, 5 verbose, 0 log-always), or
    /// null for any.
    /// </summary>
    public IReadOnlyCollection<byte>? Levels { get; init; }

    /// <summary>
    /// Provider names to keep (<c>System/Provider@Name</c>, compared ignoring case), or null for any.
    /// </summary>
    public IReadOnlyCollection<string>? Providers { get; init; }

    /// <summary>
    /// End of the <see cref="EvtxEvent.TimeCreated"/> range (exclusive), or null for no upper bound. Compared in UTC
    /// like <see cref="From"/>.
    /// </summary>
    public DateTime? To { get; init; }

    /// <summary>
    /// Last record ID to keep (inclusive), or null for no upper bound.
    /// </summary>
    public ulong? ToRecordId { get; init; }

    #endregion
}

/// <summary>
/// An <see cref="EvtxFilter"/> compiled for per-record checks: event IDs and levels as bitsets, providers as a
/// case-insensitive set, times as UTC ticks. Compiled once per filter instance (cached by reference, so the public
/// record's value equality is untouched) and shared by every thread.
/// </summary>
internal sealed class RecordFilter
{
    #region Constructors And Destructors

    /// <summary>
    /// Compiles a filter.
    /// </summary>
    /// <param name="filter">The filter.</param>
    private RecordFilter(EvtxFilter filter)
    {
        _fromId = filter.FromRecordId ?? 0;
        _toId = filter.ToRecordId ?? ulong.MaxValue;
        _fromTicks = filter.From is { } from ? ToUtc(from).Ticks : long.MinValue;
        _toTicks = filter.To is { } to ? ToUtc(to).Ticks : long.MaxValue;
        if (filter.EventIds != null)
        {
            _eventIds = new ulong[65536 / 64];
            foreach (ushort id in filter.EventIds)
                _eventIds[id >> 6] |= 1UL << (id & 63);
        }

        if (filter.Levels != null)
        {
            _levels = new ulong[256 / 64];
            foreach (byte level in filter.Levels)
                _levels[level >> 6] |= 1UL << (level & 63);
        }

        if (filter.Providers != null)
            _providers = new HashSet<string>(filter.Providers, StringComparer.OrdinalIgnoreCase);

        NeedsFields = (_eventIds != null) || (_levels != null) || (_providers != null) ||
                      (filter.From != null) || (filter.To != null);
    }

    #endregion

    #region Properties

    /// <summary>
    /// True if the filter checks System fields, so they must be read for every record.
    /// </summary>
    public bool NeedsFields { get; }

    #endregion

    #region Public Methods

    /// <summary>
    /// The compiled form of <paramref name="filter"/>, compiled on first use.
    /// </summary>
    /// <param name="filter">The filter, or null.</param>
    /// <returns>The compiled filter, or null for no filter.</returns>
    public static RecordFilter? For(EvtxFilter? filter) =>
        filter == null ? null : _compiled.GetValue(filter, static f => new RecordFilter(f));

    /// <summary>
    /// True if no record of a chunk can match its header's record-ID range (when plausible: last at or after first).
    /// The first record ID is trusted as a lower bound: a chunk starting after the filter's range is skipped. The last
    /// record ID is trusted only for a chunk that isn't flagged dirty: a chunk still being written when the log was
    /// copied can hold records past the last ID its header shows (seen in the RdpCoreTS and RemoteConnectionManager
    /// samples, where dirty chunks hold records ~60,000 IDs past their header's last).
    /// </summary>
    /// <param name="header">The chunk header.</param>
    /// <returns>True to skip the chunk.</returns>
    public bool SkipsChunk(in EvtxChunkHeader header) =>
        (header.LastEventRecordId >= header.FirstEventRecordId) &&
        ((header.FirstEventRecordId > _toId) ||
         ((header.LastEventRecordId < _fromId) && ((header.Flags & ChunkFlags.Dirty) == 0)));

    /// <summary>
    /// Checks a record's System fields.
    /// </summary>
    /// <param name="fields">The record's fields.</param>
    /// <param name="writtenFileTime">The record header's written time (FILETIME), used when it has no TimeCreated.</param>
    /// <returns>True if the record passes.</returns>
    public bool MatchesFields(in SystemFields fields, ulong writtenFileTime)
    {
        if ((_eventIds != null) && ((_eventIds[fields.EventId >> 6] & (1UL << (fields.EventId & 63))) == 0))
            return false;
        if ((_levels != null) && ((_levels[fields.Level >> 6] & (1UL << (fields.Level & 63))) == 0))
            return false;
        if ((_providers != null) && ((fields.Provider == null) || !_providers.Contains(fields.Provider)))
            return false;
        if ((_fromTicks != long.MinValue) || (_toTicks != long.MaxValue))
        {
            long ticks;
            if ((fields.Present & SystemPresence.TimeCreated) != 0)
                ticks = fields.TimeCreatedTicks;
            else if (writtenFileTime <= _maxFileTime)
                ticks = DateTime.FromFileTimeUtc((long)writtenFileTime).Ticks;
            else
                return false;
            if ((ticks < _fromTicks) || (ticks >= _toTicks))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks a record ID against the record-ID range.
    /// </summary>
    /// <param name="recordId">The record ID.</param>
    /// <returns>True if the record passes.</returns>
    public bool MatchesRecordId(ulong recordId) => (recordId >= _fromId) && (recordId <= _toId);

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// A time as UTC (unspecified is taken as UTC).
    /// </summary>
    /// <param name="time">The time.</param>
    /// <returns>The UTC time.</returns>
    private static DateTime ToUtc(DateTime time) =>
        time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc);

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Largest FILETIME <see cref="DateTime.FromFileTimeUtc"/> accepts (9999-12-31T23:59:59.9999999Z).
    /// </summary>
    private const ulong _maxFileTime = 2_650_467_743_999_999_999UL;

    /// <summary>
    /// Compiled filters by filter instance (reference identity; entries go away with their filter).
    /// </summary>
    private static readonly ConditionalWeakTable<EvtxFilter, RecordFilter> _compiled = new();

    /// <summary>
    /// Bit per event ID to keep, or null for any.
    /// </summary>
    private readonly ulong[]? _eventIds;

    /// <summary>
    /// First record ID to keep.
    /// </summary>
    private readonly ulong _fromId;

    /// <summary>
    /// Earliest TimeCreated to keep, in UTC ticks (<see cref="long.MinValue"/> for none).
    /// </summary>
    private readonly long _fromTicks;

    /// <summary>
    /// Bit per level to keep, or null for any.
    /// </summary>
    private readonly ulong[]? _levels;

    /// <summary>
    /// Provider names to keep (case-insensitive), or null for any.
    /// </summary>
    private readonly HashSet<string>? _providers;

    /// <summary>
    /// Last record ID to keep.
    /// </summary>
    private readonly ulong _toId;

    /// <summary>
    /// End of the TimeCreated range in UTC ticks, exclusive (<see cref="long.MaxValue"/> for none).
    /// </summary>
    private readonly long _toTicks;

    #endregion
}
