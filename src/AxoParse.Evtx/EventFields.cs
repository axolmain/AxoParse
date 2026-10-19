namespace AxoParse.Evtx;

/// <summary>
/// Which optional <see cref="SystemFields"/> values are present.
/// </summary>
[Flags]
internal enum SystemPresence : byte
{
    /// <summary>
    /// None of the optional values.
    /// </summary>
    None = 0,

    /// <summary>
    /// <see cref="SystemFields.ProviderGuid"/> is set.
    /// </summary>
    ProviderGuid = 0x1,

    /// <summary>
    /// <see cref="SystemFields.ActivityId"/> is set.
    /// </summary>
    ActivityId = 0x2,

    /// <summary>
    /// <see cref="SystemFields.TimeCreatedTicks"/> is set.
    /// </summary>
    TimeCreated = 0x4,
}

/// <summary>
/// The typed <c>System</c> fields of one event, read from its template's substitution values (see
/// <see cref="BinXml.TemplateFields"/>). Absent fields keep their default.
/// </summary>
internal struct SystemFields
{
    /// <summary>
    /// <c>Provider@Name</c>.
    /// </summary>
    public string? Provider;

    /// <summary>
    /// <c>Channel</c>.
    /// </summary>
    public string? Channel;

    /// <summary>
    /// <c>Computer</c>.
    /// </summary>
    public string? Computer;

    /// <summary>
    /// <c>Security@UserID</c> in SDDL form (<c>S-1-5-18</c>).
    /// </summary>
    public string? UserId;

    /// <summary>
    /// <c>Keywords</c>.
    /// </summary>
    public ulong Keywords;

    /// <summary>
    /// <c>TimeCreated@SystemTime</c> as UTC <see cref="DateTime.Ticks"/>.
    /// </summary>
    public long TimeCreatedTicks;

    /// <summary>
    /// <c>Provider@Guid</c>.
    /// </summary>
    public Guid ProviderGuid;

    /// <summary>
    /// <c>Correlation@ActivityID</c>.
    /// </summary>
    public Guid ActivityId;

    /// <summary>
    /// <c>Execution@ProcessID</c>.
    /// </summary>
    public uint ProcessId;

    /// <summary>
    /// <c>Execution@ThreadID</c>.
    /// </summary>
    public uint ThreadId;

    /// <summary>
    /// <c>EventID</c>.
    /// </summary>
    public ushort EventId;

    /// <summary>
    /// <c>Task</c>.
    /// </summary>
    public ushort Task;

    /// <summary>
    /// <c>Version</c>.
    /// </summary>
    public byte Version;

    /// <summary>
    /// <c>Level</c>.
    /// </summary>
    public byte Level;

    /// <summary>
    /// <c>Opcode</c>.
    /// </summary>
    public byte Opcode;

    /// <summary>
    /// Which optional values are present.
    /// </summary>
    public SystemPresence Present;
}

/// <summary>
/// The fields of every event of a chunk: one <see cref="SystemFields"/> per record and, when EventData was requested,
/// every record's name/value pairs back to back (names shared with the templates, values as UTF-8 in one buffer).
/// </summary>
/// <param name="system">System fields per record.</param>
/// <param name="pairEnds">Per record, the end index of its pairs in <paramref name="names"/>; null when pairs were not collected.</param>
/// <param name="names">Pair names.</param>
/// <param name="valueEnds">Per pair, the end offset of its value in <paramref name="text"/>.</param>
/// <param name="text">Pair values as UTF-8, back to back.</param>
internal sealed class ChunkFields(SystemFields[] system, int[]? pairEnds, string[] names, int[] valueEnds, byte[] text)
{
    /// <summary>
    /// System fields per record.
    /// </summary>
    public readonly SystemFields[] System = system;

    /// <summary>
    /// Per record, the end index of its pairs; null when EventData pairs were not collected.
    /// </summary>
    public readonly int[]? PairEnds = pairEnds;

    /// <summary>
    /// Pair names.
    /// </summary>
    public readonly string[] Names = names;

    /// <summary>
    /// Per pair, the end offset of its value in <see cref="Text"/>.
    /// </summary>
    public readonly int[] ValueEnds = valueEnds;

    /// <summary>
    /// Pair values as UTF-8, back to back.
    /// </summary>
    public readonly byte[] Text = text;
}

/// <summary>
/// Collects the fields of a chunk's records as they are read, then copies them into an exact-size
/// <see cref="ChunkFields"/>. Reused per thread across chunks. Strings that repeat from one record to the next
/// (provider, channel, computer, user) are reused rather than allocated again.
/// </summary>
internal sealed class FieldSink
{
    #region Properties

    /// <summary>
    /// True to collect EventData/UserData pairs as well as System fields.
    /// </summary>
    public bool CollectPairs { get; private set; }

    #endregion

    #region Fields

    /// <summary>
    /// Fields of the record being read.
    /// </summary>
    public SystemFields Current;

    /// <summary>
    /// Last <see cref="SystemFields.Provider"/>.
    /// </summary>
    public string? LastProvider;

    /// <summary>
    /// Last <see cref="SystemFields.Channel"/>.
    /// </summary>
    public string? LastChannel;

    /// <summary>
    /// Last <see cref="SystemFields.Computer"/>.
    /// </summary>
    public string? LastComputer;

    /// <summary>
    /// Last <see cref="SystemFields.UserId"/>.
    /// </summary>
    public string? LastUserId;

    /// <summary>
    /// SID bytes <see cref="LastUserId"/> was formatted from.
    /// </summary>
    public byte[] LastUserIdBytes = [];

    #endregion

    #region Public Methods

    /// <summary>
    /// Adds a pair whose value is written next with <see cref="ValueSpan"/> and <see cref="EndValue"/>.
    /// </summary>
    /// <param name="name">Pair name.</param>
    public void BeginPair(string name)
    {
        if (_pairCount == _names.Length)
        {
            Array.Resize(ref _names, _names.Length * 2);
            Array.Resize(ref _valueEnds, _valueEnds.Length * 2);
        }

        _names[_pairCount] = name;
    }

    /// <summary>
    /// Starts a record: clears <see cref="Current"/> and remembers where its pairs start.
    /// </summary>
    public void BeginRecord()
    {
        Current = default;
        _recordPairStart = _pairCount;
        _recordTextStart = _textLength;
    }

    /// <summary>
    /// Copies the collected fields into exact-size arrays for a chunk.
    /// </summary>
    /// <returns>The chunk's fields.</returns>
    public ChunkFields Build()
    {
        SystemFields[] system = _system.AsSpan(0, _recordCount).ToArray();
        if (!CollectPairs)
            return new ChunkFields(system, null, [], [], []);

        return new ChunkFields(system, _pairEnds.AsSpan(0, _recordCount).ToArray(), _names.AsSpan(0, _pairCount).ToArray(),
            _valueEnds.AsSpan(0, _pairCount).ToArray(), _text.AsSpan(0, _textLength).ToArray());
    }

    /// <summary>
    /// Keeps the current record's fields.
    /// </summary>
    public void CommitRecord()
    {
        if (_recordCount == _system.Length)
        {
            Array.Resize(ref _system, _system.Length * 2);
            Array.Resize(ref _pairEnds, _pairEnds.Length * 2);
        }

        _system[_recordCount] = Current;
        _pairEnds[_recordCount] = _pairCount;
        _recordCount++;
    }

    /// <summary>
    /// Drops the current record's pairs (a record that isn't kept).
    /// </summary>
    public void DiscardRecord()
    {
        _pairCount = _recordPairStart;
        _textLength = _recordTextStart;
    }

    /// <summary>
    /// Ends the pair begun by <see cref="BeginPair"/> after <paramref name="written"/> bytes of its value.
    /// </summary>
    /// <param name="written">Bytes written into <see cref="ValueSpan"/>'s span, or copied by <see cref="AppendValue"/>.</param>
    public void EndValue(int written)
    {
        _textLength += written;
        _valueEnds[_pairCount++] = _textLength;
    }

    /// <summary>
    /// Appends value bytes to the pair being written (for values longer than <see cref="ValueSpan"/>'s span).
    /// </summary>
    /// <param name="utf8">Value bytes.</param>
    /// <returns>Number of bytes appended.</returns>
    public int AppendValue(ReadOnlySpan<byte> utf8)
    {
        utf8.CopyTo(ValueSpan(utf8.Length));
        return utf8.Length;
    }

    /// <summary>
    /// Returns <paramref name="last"/> if it equals <paramref name="text"/>, otherwise a new string, remembered in
    /// <paramref name="last"/>.
    /// </summary>
    /// <param name="last">The previous string of this field.</param>
    /// <param name="text">The new value.</param>
    /// <returns>The string for <paramref name="text"/>.</returns>
    public static string Intern(ref string? last, scoped ReadOnlySpan<char> text)
    {
        if ((last != null) && text.SequenceEqual(last))
            return last;
        return last = new string(text);
    }

    /// <summary>
    /// Prepares for a new chunk.
    /// </summary>
    /// <param name="collectPairs">True to collect EventData/UserData pairs.</param>
    public void Reset(bool collectPairs)
    {
        CollectPairs = collectPairs;
        _recordCount = 0;
        _pairCount = 0;
        _textLength = 0;
    }

    /// <summary>
    /// Free space of at least <paramref name="minimum"/> bytes after the text written so far.
    /// </summary>
    /// <param name="minimum">Bytes needed.</param>
    /// <returns>The free space.</returns>
    public Span<byte> ValueSpan(int minimum)
    {
        if (_text.Length - _textLength < minimum)
            Array.Resize(ref _text, Math.Max(_text.Length * 2, _textLength + minimum));
        return _text.AsSpan(_textLength);
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Pair names.
    /// </summary>
    private string[] _names = new string[256];

    /// <summary>
    /// Pairs collected so far in the chunk.
    /// </summary>
    private int _pairCount;

    /// <summary>
    /// Per record, the end index of its pairs.
    /// </summary>
    private int[] _pairEnds = new int[256];

    /// <summary>
    /// Records committed so far in the chunk.
    /// </summary>
    private int _recordCount;

    /// <summary>
    /// <see cref="_pairCount"/> when the current record began.
    /// </summary>
    private int _recordPairStart;

    /// <summary>
    /// <see cref="_textLength"/> when the current record began.
    /// </summary>
    private int _recordTextStart;

    /// <summary>
    /// System fields per committed record.
    /// </summary>
    private SystemFields[] _system = new SystemFields[256];

    /// <summary>
    /// Pair values as UTF-8.
    /// </summary>
    private byte[] _text = new byte[16 * 1024];

    /// <summary>
    /// Bytes of <see cref="_text"/> in use.
    /// </summary>
    private int _textLength;

    /// <summary>
    /// Per pair, the end offset of its value.
    /// </summary>
    private int[] _valueEnds = new int[256];

    #endregion
}
