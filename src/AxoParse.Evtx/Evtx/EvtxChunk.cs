using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using AxoParse.Evtx.BinXml;

namespace AxoParse.Evtx.Evtx;

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
/// The file body consists of sequentially laid-out chunks, each exactly 64 KB. Each chunk is a self-contained unit with
/// its own header, a sequence of event records encoded in Binary XML, and trailing unused space. Chunks maintain their
/// own string and template caches for deduplication within the chunk boundary. The chunk header includes CRC32
/// checksums for both the header and the event record data area.
/// </summary>
public class EvtxChunk
{
    #region Constructors And Destructors

    /// <summary>
    /// Constructs an EvtxChunk from pre-parsed components.
    /// </summary>
    /// <param name="header">Parsed chunk header.</param>
    /// <param name="templates">Template definitions keyed by chunk-relative offset.</param>
    /// <param name="records">Parsed event records.</param>
    /// <param name="format">Format of <paramref name="output"/>, or null when records were not rendered.</param>
    /// <param name="output">Rendered UTF-8 output of every record, back to back.</param>
    /// <param name="recordEnds">End offset in <paramref name="output"/> of each record's output (record i spans
    /// <c>recordEnds[i - 1]..recordEnds[i]</c>, starting at 0).</param>
    /// <param name="renderDiagnostics">Diagnostic messages for failed renders, keyed by record index.</param>
    private EvtxChunk(EvtxChunkHeader header, Dictionary<uint, BinXmlTemplateDefinition> templates,
                      List<EvtxRecord> records, OutputFormat? format, byte[] output, int[] recordEnds,
                      Dictionary<int, string>? renderDiagnostics = null)
    {
        Header = header;
        Templates = templates;
        Records = records;
        RecordOutputList utf8 = new(output, recordEnds);
        ParsedUtf8 = utf8;
        ParsedXml = format == OutputFormat.Xml ? new RecordXmlList(utf8) : Array.Empty<string>();
        ParsedJson = format == OutputFormat.Json ? utf8 : null;
        RenderDiagnostics = renderDiagnostics ?? new Dictionary<int, string>();
    }

    #endregion

    #region Properties

    /// <summary>
    /// Parsed chunk header containing record ranges, offsets, checksums, and flags.
    /// </summary>
    public EvtxChunkHeader Header { get; }

    /// <summary>
    /// BinXml-rendered UTF-8 JSON, one slice per record, in the same order as <see cref="Records"/>.
    /// Slices share the chunk's single output buffer. Null when output format is XML.
    /// </summary>
    public IReadOnlyList<ReadOnlyMemory<byte>>? ParsedJson { get; }

    /// <summary>
    /// Rendered output of each record as UTF-8 (XML or JSON, whichever was requested), in the same order as
    /// <see cref="Records"/>. Slices share the chunk's single output buffer; prefer this over <see cref="ParsedXml"/>
    /// when the output is written or hashed rather than inspected as text. Empty when records were not rendered.
    /// </summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> ParsedUtf8 { get; }

    /// <summary>
    /// BinXml-rendered XML strings, one per record, in the same order as <see cref="Records"/>.
    /// Each string is decoded from <see cref="ParsedUtf8"/> on first access and then cached.
    /// Empty when output format is JSON.
    /// </summary>
    public IReadOnlyList<string> ParsedXml { get; }

    /// <summary>
    /// Event records parsed from this chunk's data area.
    /// </summary>
    public IReadOnlyList<EvtxRecord> Records { get; }

    /// <summary>
    /// Diagnostic messages for events where BinXml rendering failed, keyed by record index.
    /// Empty if all events rendered successfully.
    /// </summary>
    public IReadOnlyDictionary<int, string> RenderDiagnostics { get; }

    /// <summary>
    /// Template definitions preloaded from this chunk's 32-entry template pointer table,
    /// keyed by chunk-relative offset.
    /// </summary>
    public IReadOnlyDictionary<uint, BinXmlTemplateDefinition> Templates { get; }

    #endregion

    #region Public Methods

    /// <summary>
    /// Parses a 64KB chunk without BinXml parsing (header + templates + records only).
    /// </summary>
    /// <param name="chunkData">Exactly 64KB span covering the chunk.</param>
    /// <param name="chunkFileOffset">Absolute byte offset of this chunk within the EVTX file.</param>
    /// <returns>A parsed <see cref="EvtxChunk"/> with empty rendered output arrays.</returns>
    public static EvtxChunk Parse(ReadOnlySpan<byte> chunkData, int chunkFileOffset)
    {
        EvtxChunkHeader header = EvtxChunkHeader.ParseEvtxChunkHeader(chunkData);

        Dictionary<uint, BinXmlTemplateDefinition> templates =
            BinXmlTemplateDefinition.PreloadFromChunk(chunkData,
                MemoryMarshal.Cast<byte, uint>(chunkData.Slice(384, 128)), chunkFileOffset);

        int expectedRecords = (int)(header.LastEventRecordId - header.FirstEventRecordId + 1);
        List<EvtxRecord> records = ReadRecords(chunkData, chunkFileOffset, header.FreeSpaceOffset, expectedRecords);

        return new EvtxChunk(header, templates, records, null, Array.Empty<byte>(), Array.Empty<int>());
    }

    /// <summary>
    /// Parses a standalone 64KB chunk byte array with BinXml rendering.
    /// Treats the array as a self-contained "file" with all offsets chunk-relative
    /// (chunkFileOffset = 0). Creates an isolated compiled template cache per call.
    /// Suitable for WASM interop where JS passes one chunk at a time.
    /// </summary>
    /// <param name="chunkData">Exactly 64KB byte array covering a single chunk.</param>
    /// <returns>A fully parsed <see cref="EvtxChunk"/> with rendered XML output.</returns>
    public static EvtxChunk ParseStandalone(byte[] chunkData)
    {
        return Parse(chunkData, chunkFileOffset: 0,
            new Dictionary<Guid, CompiledTemplate?>(),
            compiledJsonCache: null, OutputFormat.Xml);
    }

    /// <summary>
    /// Returns the <see cref="EvtxEvent"/> at the specified index within this chunk,
    /// pairing record metadata with rendered output and any diagnostic info.
    /// </summary>
    /// <param name="index">Zero-based index into <see cref="Records"/>.</param>
    /// <returns>The event at the given index.</returns>
    public EvtxEvent GetEvent(int index)
    {
        EvtxRecord record = Records[index];
        RenderDiagnostics.TryGetValue(index, out string? diagnostic);

        if (ParsedJson is not null)
        {
            return new EvtxEvent(
                Record: record,
                Xml: string.Empty,
                Json: ParsedJson[index],
                Diagnostic: diagnostic);
        }

        return new EvtxEvent(
            Record: record,
            Xml: ParsedXml[index],
            Json: ReadOnlyMemory<byte>.Empty,
            Diagnostic: diagnostic);
    }

    #endregion

    #region Fields

    /// <summary>
    /// Size of each chunk in bytes (64 KB).
    /// </summary>
    public const int ChunkSize = 65536;

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Parses a 64KB chunk: header, preloads templates, walks event records, and parses BinXml.
    /// </summary>
    /// <param name="chunkData">Exactly 64KB span covering the chunk.</param>
    /// <param name="chunkFileOffset">Absolute byte offset of this chunk within the EVTX file.</param>
    /// <param name="fileData">Complete EVTX file bytes (needed by BinXml parser for cross-chunk template references).</param>
    /// <param name="compiledCache">Thread-safe cache of compiled XML templates shared across chunks. Null when using JSON output.</param>
    /// <param name="compiledJsonCache">Thread-safe cache of compiled JSON templates shared across chunks. Null when using XML output.</param>
    /// <param name="format">Output format for rendered event records.</param>
    /// <returns>A fully parsed <see cref="EvtxChunk"/> with rendered output.</returns>
    internal static EvtxChunk Parse(ReadOnlySpan<byte> chunkData, int chunkFileOffset,
                                    byte[] fileData,
                                    Dictionary<Guid, CompiledTemplate?>? compiledCache,
                                    Dictionary<Guid, CompiledJsonTemplate?>? compiledJsonCache,
                                    OutputFormat format = OutputFormat.Xml)
    {
        EvtxChunkHeader header = EvtxChunkHeader.ParseEvtxChunkHeader(chunkData);

        // Read template ptrs inline from the chunk span — no array allocation
        Dictionary<uint, BinXmlTemplateDefinition> templates =
            BinXmlTemplateDefinition.PreloadFromChunk(chunkData,
                MemoryMarshal.Cast<byte, uint>(chunkData.Slice(384, 128)), chunkFileOffset);

        int expectedRecords = (int)(header.LastEventRecordId - header.FirstEventRecordId + 1);
        List<EvtxRecord> records = ReadRecords(chunkData, chunkFileOffset, header.FreeSpaceOffset, expectedRecords);

        BinXmlParser binXml = new(fileData, chunkFileOffset, templates, compiledCache, compiledJsonCache);
        return RenderRecords(header, templates, records, binXml, format, headerless: false)!;
    }

    /// <summary>
    /// Parses a 64KB chunk from a byte[] — safe for use from Parallel.For lambdas
    /// (ReadOnlySpan is a ref struct and cannot cross thread boundaries).
    /// </summary>
    /// <param name="fileData">Complete EVTX file bytes.</param>
    /// <param name="chunkFileOffset">Absolute byte offset of this chunk within the EVTX file.</param>
    /// <param name="compiledCache">Thread-local cache of compiled XML templates. Null when using JSON output.</param>
    /// <param name="compiledJsonCache">Thread-local cache of compiled JSON templates. Null when using XML output.</param>
    /// <param name="format">Output format for rendered event records.</param>
    /// <returns>A fully parsed <see cref="EvtxChunk"/> with rendered output.</returns>
    internal static EvtxChunk Parse(byte[] fileData,
                                    int chunkFileOffset,
                                    Dictionary<Guid, CompiledTemplate?>? compiledCache,
                                    Dictionary<Guid, CompiledJsonTemplate?>? compiledJsonCache,
                                    OutputFormat format)
    {
        ReadOnlySpan<byte> chunkData = fileData.AsSpan(chunkFileOffset, ChunkSize);
        return Parse(chunkData, chunkFileOffset, fileData, compiledCache, compiledJsonCache, format);
    }

    /// <summary>
    /// Attempts to recover records from a 64KB region with a destroyed chunk header.
    /// Templates are unavailable from the header, but the compiled cache from other chunks
    /// may resolve cross-references. Returns null if no records are found.
    /// </summary>
    /// <param name="fileData">Complete EVTX file bytes.</param>
    /// <param name="chunkFileOffset">Absolute byte offset of this 64KB region within the file.</param>
    /// <param name="compiledCache">Thread-safe cache of compiled XML templates shared across chunks. Null when using JSON output.</param>
    /// <param name="compiledJsonCache">Thread-safe cache of compiled JSON templates shared across chunks. Null when using XML output.</param>
    /// <param name="format">Output format for rendered event records.</param>
    /// <returns>A recovered <see cref="EvtxChunk"/> with rendered output, or null if no records found.</returns>
    internal static EvtxChunk? ParseHeaderless(byte[] fileData, int chunkFileOffset,
                                               Dictionary<Guid, CompiledTemplate?>? compiledCache,
                                               Dictionary<Guid, CompiledJsonTemplate?>? compiledJsonCache,
                                               OutputFormat format = OutputFormat.Xml)
    {
        ReadOnlySpan<byte> chunkData = fileData.AsSpan(chunkFileOffset, ChunkSize);
        List<EvtxRecord> records = ReadRecords(chunkData, chunkFileOffset, (uint)chunkData.Length);
        if (records.Count == 0)
            return null;

        Dictionary<uint, BinXmlTemplateDefinition> templates = new();
        BinXmlParser binXml = new(fileData, chunkFileOffset, templates, compiledCache, compiledJsonCache);
        return RenderRecords(default, templates, records, binXml, format, headerless: true);
    }

    /// <summary>
    /// Renders every record of the chunk at <paramref name="chunkFileOffset"/> and appends the UTF-8 output to
    /// <paramref name="sink"/> in record order. Produces the same bytes as concatenating the rendered output of
    /// <see cref="Parse(byte[], int, Dictionary{Guid, CompiledTemplate?}?, Dictionary{Guid, CompiledJsonTemplate?}?, OutputFormat)"/>
    /// (or <see cref="ParseHeaderless"/> when <paramref name="headerless"/> is true), without retaining any record output.
    /// Records whose rendering throws contribute no bytes, matching the empty output those parses store for them.
    /// </summary>
    /// <param name="fileData">Complete EVTX file bytes.</param>
    /// <param name="chunkFileOffset">Absolute byte offset of this 64KB region within the file.</param>
    /// <param name="headerless">True to recover records from a region whose chunk header is damaged or missing.</param>
    /// <param name="compiledCache">Cache of compiled XML templates. Null when using JSON output.</param>
    /// <param name="compiledJsonCache">Cache of compiled JSON templates. Null when using XML output.</param>
    /// <param name="format">Output format for rendered event records.</param>
    /// <param name="sink">Buffer that receives the UTF-8 output.</param>
    /// <returns>
    /// Records the equivalent parse would keep: every record of a normal chunk; for a headerless region,
    /// only records that rendered non-empty output.
    /// </returns>
    internal static int RenderTo(byte[] fileData, int chunkFileOffset, bool headerless,
                                 Dictionary<Guid, CompiledTemplate?>? compiledCache,
                                 Dictionary<Guid, CompiledJsonTemplate?>? compiledJsonCache,
                                 OutputFormat format, ArrayBufferWriter<byte> sink)
    {
        ReadOnlySpan<byte> chunkData = fileData.AsSpan(chunkFileOffset, ChunkSize);
        Dictionary<uint, BinXmlTemplateDefinition> templates;
        uint dataEnd;
        if (headerless)
        {
            templates = new Dictionary<uint, BinXmlTemplateDefinition>();
            dataEnd = (uint)chunkData.Length;
        }
        else
        {
            EvtxChunkHeader header = EvtxChunkHeader.ParseEvtxChunkHeader(chunkData);
            templates = BinXmlTemplateDefinition.PreloadFromChunk(chunkData,
                MemoryMarshal.Cast<byte, uint>(chunkData.Slice(384, 128)), chunkFileOffset);
            dataEnd = header.FreeSpaceOffset;
        }

        // Render each record as soon as the walk finds it, while its bytes are still in cache. The parser is created
        // on the first record, as Parse/ParseHeaderless only construct it once records exist (its constructor
        // validates the common string table and can throw on a corrupt one).
        uint scanEnd = Math.Min(dataEnd, (uint)chunkData.Length);
        BinXmlParser? binXml = null;
        int kept = 0;
        int offset = _chunkHeaderSize;
        while (TryReadNextRecord(chunkData, chunkFileOffset, scanEnd, ref offset, out EvtxRecord record))
        {
            binXml ??= new BinXmlParser(fileData, chunkFileOffset, templates, compiledCache, compiledJsonCache);
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
                offset += declaredSize >= 28 ? (int)declaredSize : 4;
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
    /// (a failed record keeps empty output). Records are rendered into a reusable per-thread scratch buffer and then
    /// copied once into an exact-size array, so a chunk costs one allocation instead of one per record.
    /// </summary>
    /// <param name="header">Chunk header (default for a headerless recovery region).</param>
    /// <param name="templates">Template definitions keyed by chunk-relative offset.</param>
    /// <param name="records">Records to render.</param>
    /// <param name="binXml">Configured BinXml parser for this chunk.</param>
    /// <param name="format">Output format (XML or JSON).</param>
    /// <param name="headerless">True for a recovery region: only records that render non-empty output are kept,
    /// and null is returned if there are none.</param>
    /// <returns>The rendered chunk, or null for a headerless region with no renderable records.</returns>
    private static EvtxChunk? RenderRecords(EvtxChunkHeader header, Dictionary<uint, BinXmlTemplateDefinition> templates,
                                            List<EvtxRecord> records, BinXmlParser binXml, OutputFormat format,
                                            bool headerless)
    {
        ArrayBufferWriter<byte> scratch = _threadRenderScratch ??= new ArrayBufferWriter<byte>(_initialScratchBytes);
        scratch.ResetWrittenCount();
        List<EvtxRecord> kept = headerless ? new List<EvtxRecord>(records.Count) : records;
        int[] recordEnds = new int[records.Count];
        Dictionary<int, string> diagnostics = new();
        int count = 0;
        for (int i = 0; i < records.Count; i++)
        {
            int before = scratch.WrittenCount;
            string? diagnostic = null;
            try
            {
                binXml.AppendRecordUtf8(records[i], format, scratch);
            }
            catch (Exception ex)
            {
                diagnostic = $"BinXml render failed: {ex.Message}";
            }

            if (headerless)
            {
                // Recovery keeps only records that produced output; a failed render produced none
                if (scratch.WrittenCount == before)
                    continue;
                kept.Add(records[i]);
            }

            if (diagnostic != null)
                diagnostics[count] = diagnostic;
            recordEnds[count++] = scratch.WrittenCount;
        }

        if (headerless && (count == 0))
            return null;
        if (count != recordEnds.Length)
            Array.Resize(ref recordEnds, count);

        byte[] output = GC.AllocateUninitializedArray<byte>(scratch.WrittenCount);
        scratch.WrittenSpan.CopyTo(output);
        return new EvtxChunk(header, templates, kept, format, output, recordEnds, diagnostics);
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

    #endregion

    #region Nested Types

    /// <summary>
    /// Per-record views over a chunk's single UTF-8 output buffer.
    /// </summary>
    /// <param name="output">Rendered output of every record, back to back.</param>
    /// <param name="recordEnds">End offset of each record's output.</param>
    private sealed class RecordOutputList(byte[] output, int[] recordEnds) : IReadOnlyList<ReadOnlyMemory<byte>>
    {
        /// <summary>
        /// Number of records.
        /// </summary>
        public int Count => recordEnds.Length;

        /// <summary>
        /// The UTF-8 output of record <paramref name="index"/>.
        /// </summary>
        /// <param name="index">Zero-based record index.</param>
        public ReadOnlyMemory<byte> this[int index]
        {
            get
            {
                int start = index == 0 ? 0 : recordEnds[index - 1];
                return new ReadOnlyMemory<byte>(output, start, recordEnds[index] - start);
            }
        }

        /// <summary>
        /// Enumerates each record's UTF-8 output in order.
        /// </summary>
        /// <returns>An enumerator over the records' output.</returns>
        public IEnumerator<ReadOnlyMemory<byte>> GetEnumerator()
        {
            for (int i = 0; i < recordEnds.Length; i++)
                yield return this[i];
        }

        /// <summary>
        /// Non-generic enumerator.
        /// </summary>
        /// <returns>An enumerator over the records' output.</returns>
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// XML strings decoded from a chunk's UTF-8 output on first access, then cached.
    /// </summary>
    /// <param name="utf8">Per-record UTF-8 output.</param>
    private sealed class RecordXmlList(RecordOutputList utf8) : IReadOnlyList<string>
    {
        /// <summary>
        /// Number of records.
        /// </summary>
        public int Count => utf8.Count;

        /// <summary>
        /// The XML of record <paramref name="index"/>.
        /// </summary>
        /// <param name="index">Zero-based record index.</param>
        // Benign race: concurrent first reads decode identical strings
        public string this[int index] => (_strings ??= new string?[utf8.Count])[index] ??= Encoding.UTF8.GetString(utf8[index].Span);

        /// <summary>
        /// Enumerates each record's XML in order.
        /// </summary>
        /// <returns>An enumerator over the records' XML.</returns>
        public IEnumerator<string> GetEnumerator()
        {
            for (int i = 0; i < utf8.Count; i++)
                yield return this[i];
        }

        /// <summary>
        /// Non-generic enumerator.
        /// </summary>
        /// <returns>An enumerator over the records' XML.</returns>
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// Decoded strings, created on first access.
        /// </summary>
        private string?[]? _strings;
    }

    #endregion
}
