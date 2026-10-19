using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using AxoParse.Evtx.BinXml;

namespace AxoParse.Evtx;

/// <summary>
/// Chunk status flags stored at chunk header offset 120.
/// </summary>
[Flags]
public enum ChunkFlags : uint
{
    /// <summary>
    /// No flags set; chunk is in a normal state.
    /// </summary>
    None = 0x0,

    /// <summary>
    /// Chunk has been modified since last flush (0x1).
    /// </summary>
    Dirty = 0x1,

    /// <summary>
    /// CRC32 checksums are not present or should not be validated (0x4).
    /// </summary>
    NoCrc32 = 0x4,
}

/// <summary>
/// One 64 KB chunk of an EVTX file and the events parsed from it. The file body is a sequence of chunks, each a
/// self-contained unit with its own header, a sequence of event records encoded in Binary XML, and trailing unused
/// space; chunks keep their own string and template tables, so each can be decoded on its own. The chunk header holds
/// CRC32 checksums for both the header and the event record data area. Immutable and safe to read from any thread.
/// </summary>
public sealed class EvtxChunk
{
    #region Constructors And Destructors

    /// <summary>
    /// Creates a parsed chunk.
    /// </summary>
    /// <param name="header">Parsed chunk header (default for a recovered region).</param>
    /// <param name="fileOffset">Absolute file offset of the chunk.</param>
    /// <param name="recovered">True for a region whose chunk header is damaged or missing.</param>
    /// <param name="dataBase">Offset of the chunk within the array its records' offsets refer to.</param>
    /// <param name="records">Parsed event records.</param>
    /// <param name="output">Rendered UTF-8 output of every record, back to back.</param>
    /// <param name="recordEnds">End offset in <paramref name="output"/> of each record's output (record i spans
    /// <c>recordEnds[i - 1]..recordEnds[i]</c>, starting at 0), or empty when records were not rendered.</param>
    /// <param name="diagnostics">Diagnostics for records that didn't decode cleanly, keyed by record index, or null.</param>
    private EvtxChunk(EvtxChunkHeader header, long fileOffset, bool recovered, int dataBase, List<EvtxRecord> records,
                      byte[] output, int[] recordEnds, Dictionary<int, EvtxDiagnostic>? diagnostics)
    {
        Header = header;
        FileOffset = fileOffset;
        Recovered = recovered;
        _dataBase = dataBase;
        _records = records;
        _output = output;
        _recordEnds = recordEnds;
        _diagnostics = diagnostics;
        Events = new EventList(this);
    }

    #endregion

    #region Properties

    /// <summary>
    /// The chunk's events, in record order.
    /// </summary>
    public IReadOnlyList<EvtxEvent> Events { get; }

    /// <summary>
    /// Absolute file offset of the chunk.
    /// </summary>
    public long FileOffset { get; }

    /// <summary>
    /// Parsed chunk header: record number and ID ranges, offsets, checksums and flags. Default (all zero) for a
    /// <see cref="Recovered"/> region.
    /// </summary>
    public EvtxChunkHeader Header { get; }

    /// <summary>
    /// True if the region's chunk header is damaged or missing and its events were recovered by scanning for records.
    /// Only records that rendered output are kept from such a region.
    /// </summary>
    public bool Recovered { get; }

    /// <summary>
    /// Number of events in the chunk.
    /// </summary>
    internal int Count => _records.Count;

    /// <summary>
    /// The chunk's records, in record order.
    /// </summary>
    internal IReadOnlyList<EvtxRecord> Records => _records;

    #endregion

    #region Fields

    /// <summary>
    /// Size of each chunk in bytes (64 KB).
    /// </summary>
    public const int ChunkSize = 65536;

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Diagnostic of record <paramref name="index"/>, or null if it decoded cleanly.
    /// </summary>
    /// <param name="index">Zero-based record index.</param>
    /// <returns>The diagnostic, or null.</returns>
    internal EvtxDiagnostic? GetDiagnostic(int index) =>
        (_diagnostics != null) && _diagnostics.TryGetValue(index, out EvtxDiagnostic diagnostic) ? diagnostic : null;

    /// <summary>
    /// Appends this chunk's record diagnostics to <paramref name="destination"/> in record order.
    /// </summary>
    /// <param name="destination">List that receives the diagnostics.</param>
    internal void CollectDiagnostics(List<EvtxDiagnostic> destination)
    {
        if (_diagnostics == null)
            return;
        for (int i = 0; i < _records.Count; i++)
        {
            if (_diagnostics.TryGetValue(i, out EvtxDiagnostic diagnostic))
                destination.Add(diagnostic);
        }
    }

    /// <summary>
    /// UTF-8 output of record <paramref name="index"/>, or empty when the chunk wasn't rendered.
    /// </summary>
    /// <param name="index">Zero-based record index.</param>
    /// <returns>A slice of the chunk's output buffer.</returns>
    internal ReadOnlyMemory<byte> Output(int index)
    {
        if (_recordEnds.Length == 0)
            return ReadOnlyMemory<byte>.Empty;
        int start = index == 0 ? 0 : _recordEnds[index - 1];
        return new ReadOnlyMemory<byte>(_output, start, _recordEnds[index] - start);
    }

    /// <summary>
    /// Header fields of record <paramref name="index"/>.
    /// </summary>
    /// <param name="index">Zero-based record index.</param>
    /// <returns>The parsed record.</returns>
    internal EvtxRecord Record(int index) => _records[index];

    /// <summary>
    /// Absolute file offset of record <paramref name="index"/>'s header.
    /// </summary>
    /// <param name="index">Zero-based record index.</param>
    /// <returns>The offset.</returns>
    internal long RecordFileOffset(int index) =>
        FileOffset + (_records[index].EventDataFileOffset - EvtxRecord.HeaderSize - _dataBase);

    /// <summary>
    /// Reads a 64KB chunk's header and records without rendering them (no output, no diagnostics).
    /// </summary>
    /// <param name="chunkData">Exactly 64KB span covering the chunk.</param>
    /// <param name="chunkFileOffset">Absolute byte offset of this chunk within the EVTX file.</param>
    /// <returns>A chunk whose events have empty output.</returns>
    internal static EvtxChunk Parse(ReadOnlySpan<byte> chunkData, int chunkFileOffset)
    {
        EvtxChunkHeader header = EvtxChunkHeader.ParseEvtxChunkHeader(chunkData);
        int expectedRecords = ExpectedRecordCount(header);
        List<EvtxRecord> records = ReadRecords(chunkData, chunkFileOffset, header.FreeSpaceOffset, expectedRecords);
        return new EvtxChunk(header, chunkFileOffset, recovered: false, chunkFileOffset, records,
            Array.Empty<byte>(), Array.Empty<int>(), null);
    }

    /// <summary>
    /// Record-list capacity hint from the header's event record ID range. The IDs come from the file, so a corrupt
    /// header (last below first, or a span no 64 KB chunk could hold) must not size the list; records are found by
    /// walking the chunk either way, so 0 just means "unknown".
    /// </summary>
    /// <param name="header">Parsed chunk header.</param>
    /// <returns><c>Last - First + 1</c> when that is plausible, otherwise 0.</returns>
    private static int ExpectedRecordCount(EvtxChunkHeader header)
    {
        if (header.LastEventRecordId < header.FirstEventRecordId)
            return 0;
        ulong span = header.LastEventRecordId - header.FirstEventRecordId;
        return span < _maxRecordsPerChunk ? (int)span + 1 : 0;
    }

    /// <summary>
    /// Parses and renders a 64KB chunk: header, then every record's BinXml.
    /// </summary>
    /// <param name="data">Array holding the chunk (the whole file, or a 64 KB buffer).</param>
    /// <param name="at">Offset of the chunk within <paramref name="data"/>.</param>
    /// <param name="fileOffset">Absolute file offset of the chunk.</param>
    /// <param name="compiledCache">Cache of templates compiled for the output format, shared across the chunks a
    /// worker parses (never across formats), or null to decode every template body.</param>
    /// <param name="options">Output format, salvage mode and provider templates.</param>
    /// <returns>The rendered chunk.</returns>
    internal static EvtxChunk Parse(byte[] data, int at, long fileOffset,
                                    Dictionary<Guid, CompiledTemplate?>? compiledCache, EvtxOptions options)
    {
        ReadOnlySpan<byte> chunkData = data.AsSpan(at, ChunkSize);
        EvtxChunkHeader header = EvtxChunkHeader.ParseEvtxChunkHeader(chunkData);
        int expectedRecords = ExpectedRecordCount(header);
        List<EvtxRecord> records = ReadRecords(chunkData, at, header.FreeSpaceOffset, expectedRecords);

        BinXmlParser binXml = new(data, at, compiledCache, options.WevtCache, options.Salvage);
        return RenderRecords(header, fileOffset, at, records, binXml, options.Format, headerless: false)!;
    }

    /// <summary>
    /// Attempts to recover records from a 64KB region with a destroyed chunk header.
    /// Templates are unavailable from the header, but the compiled cache from other chunks
    /// may resolve cross-references. Returns null if no records are found.
    /// </summary>
    /// <param name="data">Array holding the region (the whole file, or a 64 KB buffer).</param>
    /// <param name="at">Offset of the region within <paramref name="data"/>.</param>
    /// <param name="fileOffset">Absolute file offset of the region.</param>
    /// <param name="compiledCache">Cache of templates compiled for the output format, shared across the regions a
    /// worker recovers (never across formats), or null to decode every template body.</param>
    /// <param name="options">Output format, salvage mode and provider templates.</param>
    /// <returns>A recovered chunk, or null if no record rendered.</returns>
    internal static EvtxChunk? ParseHeaderless(byte[] data, int at, long fileOffset,
                                               Dictionary<Guid, CompiledTemplate?>? compiledCache, EvtxOptions options)
    {
        ReadOnlySpan<byte> chunkData = data.AsSpan(at, ChunkSize);
        List<EvtxRecord> records = ReadRecords(chunkData, at, (uint)chunkData.Length);
        if (records.Count == 0)
            return null;

        BinXmlParser binXml = new(data, at, compiledCache, options.WevtCache, options.Salvage);
        return RenderRecords(default, fileOffset, at, records, binXml, options.Format, headerless: true);
    }

    /// <summary>
    /// Renders every record of the chunk at <paramref name="at"/> and appends the UTF-8 output to
    /// <paramref name="sink"/> in record order. Produces the same bytes as concatenating the rendered output of
    /// <see cref="Parse(byte[], int, long, Dictionary{Guid, CompiledTemplate?}?, EvtxOptions)"/> (or
    /// <see cref="ParseHeaderless"/> when <paramref name="headerless"/> is true), without retaining any record output.
    /// Records whose rendering throws contribute no bytes, matching the empty output those parses store for them.
    /// </summary>
    /// <param name="data">Array holding the chunk (the whole file, or a 64 KB buffer).</param>
    /// <param name="at">Offset of the chunk within <paramref name="data"/>.</param>
    /// <param name="headerless">True to recover records from a region whose chunk header is damaged or missing.</param>
    /// <param name="compiledCache">Cache of templates compiled for the output format, shared across the chunks a
    /// worker renders (never across formats), or null to decode every template body.</param>
    /// <param name="options">Output format, salvage mode and provider templates.</param>
    /// <param name="sink">Buffer that receives the UTF-8 output.</param>
    /// <returns>
    /// Records the equivalent parse would keep: every record of a normal chunk; for a headerless region,
    /// only records that rendered non-empty output.
    /// </returns>
    internal static int RenderTo(byte[] data, int at, bool headerless, Dictionary<Guid, CompiledTemplate?>? compiledCache,
                                 EvtxOptions options, ArrayBufferWriter<byte> sink)
    {
        OutputFormat format = options.Format;
        ReadOnlySpan<byte> chunkData = data.AsSpan(at, ChunkSize);
        // Rendering reads template definitions straight from the chunk, so the template table isn't preloaded here
        uint dataEnd = headerless
            ? (uint)chunkData.Length
            : EvtxChunkHeader.ParseEvtxChunkHeader(chunkData).FreeSpaceOffset;

        // Render each record as soon as the walk finds it, while its bytes are still in cache. The parser is created
        // on the first record, as Parse/ParseHeaderless only construct it once records exist (its constructor
        // validates the common string table and can throw on a corrupt one).
        uint scanEnd = Math.Min(dataEnd, (uint)chunkData.Length);
        BinXmlParser? binXml = null;
        int kept = 0;
        int offset = _chunkHeaderSize;
        while (TryReadNextRecord(chunkData, at, scanEnd, ref offset, out EvtxRecord record))
        {
            binXml ??= new BinXmlParser(data, at, compiledCache, options.WevtCache, options.Salvage);
            int before = sink.WrittenCount;
            try
            {
                binXml.AppendRecordUtf8(record, format, sink);
            }
            catch (Exception)
            {
                // Same policy as RenderRecords: a record that fails to render produces no output
            }

            if (!headerless || (sink.WrittenCount > before))
                kept++;
        }

        return kept;
    }

    /// <summary>
    /// Walks event records within a chunk data region, resilient to mid-chunk corruption.
    /// Scans from offset 512 (end of chunk header) up to <paramref name="dataEnd"/>,
    /// skipping non-record regions (4-byte aligned scan) and records that fail size/integrity
    /// validation. Zero-filled or corrupt regions are walked through — valid records after
    /// a corrupted gap are still recovered.
    /// </summary>
    /// <param name="chunkData">Full 64KB chunk span.</param>
    /// <param name="chunkFileOffset">Absolute file offset of this chunk.</param>
    /// <param name="dataEnd">Upper scan boundary (exclusive). Use <see cref="EvtxChunkHeader.FreeSpaceOffset"/> for normal chunks, <c>chunkData.Length</c> for headerless recovery.</param>
    /// <param name="capacityHint">Initial list capacity hint. Use expected record count when available, 0 for unknown.</param>
    /// <returns>List of successfully parsed records.</returns>
    private static List<EvtxRecord> ReadRecords(ReadOnlySpan<byte> chunkData, int chunkFileOffset,
                                                uint dataEnd, int capacityHint = 0)
    {
        uint scanEnd = Math.Min(dataEnd, (uint)chunkData.Length);
        List<EvtxRecord> records = new List<EvtxRecord>(capacityHint);

        int offset = _chunkHeaderSize;
        while (TryReadNextRecord(chunkData, chunkFileOffset, scanEnd, ref offset, out EvtxRecord record))
            records.Add(record);

        return records;
    }

    /// <summary>
    /// Advances from <paramref name="offset"/> to the next valid event record and returns it, positioning
    /// <paramref name="offset"/> just past it. Non-record bytes are skipped 4 at a time (records are 4-byte aligned);
    /// a record whose magic (0x00002A2A) matches but whose size fields are invalid is skipped by its declared size
    /// when that is at least the 28-byte minimum, otherwise by 4, so its header is never re-read as a new record.
    /// </summary>
    /// <param name="chunkData">Full 64KB chunk span.</param>
    /// <param name="chunkFileOffset">Absolute file offset of this chunk.</param>
    /// <param name="scanEnd">Upper scan boundary (exclusive), already clamped to the chunk length.</param>
    /// <param name="offset">Chunk-relative scan position; updated past the returned record or to the end.</param>
    /// <param name="record">The record found, when the method returns true.</param>
    /// <returns>True if a record was found before <paramref name="scanEnd"/>.</returns>
    private static bool TryReadNextRecord(ReadOnlySpan<byte> chunkData, int chunkFileOffset, uint scanEnd,
                                          ref int offset, out EvtxRecord record)
    {
        // 28 bytes = 24-byte record header + 4-byte trailing size copy, the smallest possible record
        while (offset + 28 <= scanEnd)
        {
            if (!chunkData.Slice(offset, 4).SequenceEqual("\x2a\x2a\x00\x00"u8))
            {
                offset += 4;
                continue;
            }

            EvtxRecord? parsed = EvtxRecord.ParseEvtxRecord(chunkData[offset..], chunkFileOffset + offset);
            if (parsed == null)
            {
                uint declaredSize = MemoryMarshal.Read<uint>(chunkData.Slice(offset + 4, 4));
                // 64-bit sum, clamped to the scan end: a declared size >= 2^31 must not wrap the offset negative
                offset = declaredSize >= 28 ? (int)Math.Min(offset + (long)declaredSize, scanEnd) : offset + 4;
                continue;
            }

            record = parsed.Value;
            offset += (int)record.Size;
            return true;
        }

        record = default;
        return false;
    }

    /// <summary>
    /// Renders every record into one UTF-8 buffer for the chunk, capturing diagnostics for records that fail
    /// (a failed record keeps empty output) or, in salvage mode, were cut short (a salvaged record keeps its prefix).
    /// Records are rendered into a reusable per-thread scratch buffer and then copied once into an exact-size array,
    /// so a chunk costs one allocation instead of one per record.
    /// </summary>
    /// <param name="header">Chunk header (default for a headerless recovery region).</param>
    /// <param name="fileOffset">Absolute file offset of the chunk.</param>
    /// <param name="at">Offset of the chunk within the array its records' offsets refer to.</param>
    /// <param name="records">Records to render.</param>
    /// <param name="binXml">Configured BinXml parser for this chunk.</param>
    /// <param name="format">Output format.</param>
    /// <param name="headerless">True for a recovery region: only records that render non-empty output are kept,
    /// and null is returned if there are none.</param>
    /// <returns>The rendered chunk, or null for a headerless region with no renderable records.</returns>
    private static EvtxChunk? RenderRecords(EvtxChunkHeader header, long fileOffset, int at, List<EvtxRecord> records,
                                            BinXmlParser binXml, OutputFormat format, bool headerless)
    {
        ArrayBufferWriter<byte> scratch = _threadRenderScratch ??= new ArrayBufferWriter<byte>(_initialScratchBytes);
        scratch.ResetWrittenCount();
        List<EvtxRecord> kept = headerless ? new List<EvtxRecord>(records.Count) : records;
        int[] recordEnds = new int[records.Count];
        Dictionary<int, EvtxDiagnostic>? diagnostics = null;
        int count = 0;
        for (int i = 0; i < records.Count; i++)
        {
            int before = scratch.WrittenCount;
            EvtxDiagnosticKind kind = default;
            string? message = null;
            try
            {
                binXml.AppendRecordUtf8(records[i], format, scratch);
                if (binXml.SalvageDiagnostic is { } salvaged)
                {
                    kind = EvtxDiagnosticKind.RecordSalvaged;
                    message = $"BinXml salvaged (output ends at the corruption): {salvaged}";
                }
            }
            catch (Exception ex)
            {
                kind = EvtxDiagnosticKind.RecordRenderFailed;
                message = $"BinXml render failed: {ex.Message}";
            }

            if (headerless)
            {
                // Recovery keeps only records that produced output; a failed render produced none
                if (scratch.WrittenCount == before)
                    continue;
                kept.Add(records[i]);
            }

            if (message != null)
            {
                EvtxRecord record = records[i];
                long recordOffset = fileOffset + (record.EventDataFileOffset - EvtxRecord.HeaderSize - at);
                (diagnostics ??= new Dictionary<int, EvtxDiagnostic>())[count] =
                    new EvtxDiagnostic(kind, record.EventRecordId, recordOffset, message);
            }
            recordEnds[count++] = scratch.WrittenCount;
        }

        if (headerless && (count == 0))
            return null;
        if (count != recordEnds.Length)
            Array.Resize(ref recordEnds, count);

        byte[] output = GC.AllocateUninitializedArray<byte>(scratch.WrittenCount);
        scratch.WrittenSpan.CopyTo(output);
        return new EvtxChunk(header, fileOffset, headerless, at, kept, output, recordEnds, diagnostics);
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Initial size of the per-thread render scratch buffer (256 KB): a 64 KB chunk typically renders to 100-200 KB.
    /// </summary>
    private const int _initialScratchBytes = 256 * 1024;

    /// <summary>
    /// Per-thread scratch buffer records are rendered into before a chunk's output is copied to its exact-size array.
    /// Safe because chunk rendering is synchronous and never re-enters on one thread.
    /// </summary>
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _threadRenderScratch;

    /// <summary>
    /// Size of the chunk header in bytes. Event record data begins immediately after.
    /// </summary>
    private const int _chunkHeaderSize = 512;

    /// <summary>
    /// Most records a chunk can hold: the 65,024 bytes after the header divided by the smallest possible record,
    /// 28 bytes (4 signature + 4 size + 8 record ID + 8 timestamp + 4 trailing size copy, with empty BinXml) = 2,322.
    /// </summary>
    private const int _maxRecordsPerChunk = (ChunkSize - _chunkHeaderSize) / 28;

    /// <summary>
    /// Offset of the chunk within the array its records' offsets (<see cref="EvtxRecord.EventDataFileOffset"/>) refer
    /// to: the chunk's file offset when parsed from a whole-file array, 0 when parsed from its own 64 KB buffer.
    /// </summary>
    private readonly int _dataBase;

    /// <summary>
    /// Diagnostics for records that didn't decode cleanly, keyed by record index; null when there are none.
    /// </summary>
    private readonly Dictionary<int, EvtxDiagnostic>? _diagnostics;

    /// <summary>
    /// Rendered output of every record, back to back.
    /// </summary>
    private readonly byte[] _output;

    /// <summary>
    /// End offset in <see cref="_output"/> of each record's output; empty when the chunk wasn't rendered.
    /// </summary>
    private readonly int[] _recordEnds;

    /// <summary>
    /// The chunk's records, in record order.
    /// </summary>
    private readonly List<EvtxRecord> _records;

    #endregion

    #region Nested Types

    /// <summary>
    /// The chunk's events as a list.
    /// </summary>
    /// <param name="chunk">The chunk.</param>
    private sealed class EventList(EvtxChunk chunk) : IReadOnlyList<EvtxEvent>
    {
        /// <summary>
        /// Number of events.
        /// </summary>
        public int Count => chunk._records.Count;

        /// <summary>
        /// The event at <paramref name="index"/>.
        /// </summary>
        /// <param name="index">Zero-based record index.</param>
        public EvtxEvent this[int index]
        {
            get
            {
                if ((uint)index >= (uint)chunk._records.Count)
                    throw new ArgumentOutOfRangeException(nameof(index));
                return new EvtxEvent(chunk, index);
            }
        }

        /// <summary>
        /// Enumerates the events in record order.
        /// </summary>
        /// <returns>An enumerator over the events.</returns>
        public IEnumerator<EvtxEvent> GetEnumerator()
        {
            for (int i = 0; i < chunk._records.Count; i++)
                yield return new EvtxEvent(chunk, i);
        }

        /// <summary>
        /// Non-generic enumerator.
        /// </summary>
        /// <returns>An enumerator over the events.</returns>
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    #endregion
}
