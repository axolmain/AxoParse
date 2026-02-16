using System.Runtime.InteropServices;
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
    /// <param name="parsedXml">Rendered XML strings (empty array when using JSON output).</param>
    /// <param name="parsedJson">Rendered JSON byte arrays, or null when using XML output.</param>
    /// <param name="renderDiagnostics">Diagnostic messages for failed renders, keyed by record index.</param>
    private EvtxChunk(EvtxChunkHeader header, Dictionary<uint, BinXmlTemplateDefinition> templates,
                      List<EvtxRecord> records, string[] parsedXml, byte[][]? parsedJson = null,
                      Dictionary<int, string>? renderDiagnostics = null)
    {
        Header = header;
        Templates = templates;
        Records = records;
        ParsedXml = parsedXml;
        ParsedJson = parsedJson;
        RenderDiagnostics = renderDiagnostics ?? new Dictionary<int, string>();
    }

    #endregion

    #region Properties

    /// <summary>
    /// Parsed chunk header containing record ranges, offsets, checksums, and flags.
    /// </summary>
    public EvtxChunkHeader Header { get; }

    /// <summary>
    /// BinXml-rendered UTF-8 JSON byte arrays, one per record, in the same order as <see cref="Records"/>.
    /// Null when output format is XML.
    /// </summary>
    public IReadOnlyList<byte[]>? ParsedJson { get; }

    /// <summary>
    /// BinXml-rendered XML strings, one per record, in the same order as <see cref="Records"/>.
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

        return new EvtxChunk(header, templates, records, Array.Empty<string>());
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
    /// <param name="compiledCache">Thread-safe cache of compiled templates shared across chunks.</param>
    /// <param name="format">Output format for rendered event records.</param>
    /// <returns>A fully parsed <see cref="EvtxChunk"/> with rendered output.</returns>
    internal static EvtxChunk Parse(ReadOnlySpan<byte> chunkData, int chunkFileOffset,
                                    byte[] fileData, Dictionary<Guid, CompiledTemplate?> compiledCache,
                                    OutputFormat format = OutputFormat.Xml)
    {
        EvtxChunkHeader header = EvtxChunkHeader.ParseEvtxChunkHeader(chunkData);

        // Read template ptrs inline from the chunk span — no array allocation
        Dictionary<uint, BinXmlTemplateDefinition> templates =
            BinXmlTemplateDefinition.PreloadFromChunk(chunkData,
                MemoryMarshal.Cast<byte, uint>(chunkData.Slice(384, 128)), chunkFileOffset);

        int expectedRecords = (int)(header.LastEventRecordId - header.FirstEventRecordId + 1);
        List<EvtxRecord> records = ReadRecords(chunkData, chunkFileOffset, header.FreeSpaceOffset, expectedRecords);

        BinXmlParser binXml = new(fileData, chunkFileOffset, templates, compiledCache);
        (string[] xml, byte[][]? json, Dictionary<int, string> diagnostics) = RenderRecords(records, binXml, format);

        return new EvtxChunk(header, templates, records, xml, json, diagnostics);
    }

    /// <summary>
    /// Parses a 64KB chunk from a byte[] — safe for use from Parallel.For lambdas
    /// (ReadOnlySpan is a ref struct and cannot cross thread boundaries).
    /// </summary>
    /// <param name="fileData">Complete EVTX file bytes.</param>
    /// <param name="chunkFileOffset">Absolute byte offset of this chunk within the EVTX file.</param>
    /// <param name="compiledCache">Thread-local cache of compiled templates.</param>
    /// <param name="format">Output format for rendered event records.</param>
    /// <returns>A fully parsed <see cref="EvtxChunk"/> with rendered output.</returns>
    internal static EvtxChunk Parse(byte[] fileData,
                                    int chunkFileOffset,
                                    Dictionary<Guid, CompiledTemplate?> compiledCache,
                                    OutputFormat format)
    {
        ReadOnlySpan<byte> chunkData = fileData.AsSpan(chunkFileOffset, ChunkSize);
        return Parse(chunkData, chunkFileOffset, fileData, compiledCache, format);
    }

    /// <summary>
    /// Attempts to recover records from a 64KB region with a destroyed chunk header.
    /// Templates are unavailable from the header, but the compiled cache from other chunks
    /// may resolve cross-references. Returns null if no records are found.
    /// </summary>
    /// <param name="fileData">Complete EVTX file bytes.</param>
    /// <param name="chunkFileOffset">Absolute byte offset of this 64KB region within the file.</param>
    /// <param name="compiledCache">Thread-safe cache of compiled templates shared across chunks.</param>
    /// <param name="format">Output format for rendered event records.</param>
    /// <returns>A recovered <see cref="EvtxChunk"/> with rendered output, or null if no records found.</returns>
    internal static EvtxChunk? ParseHeaderless(byte[] fileData, int chunkFileOffset,
                                               Dictionary<Guid, CompiledTemplate?> compiledCache,
                                               OutputFormat format = OutputFormat.Xml)
    {
        ReadOnlySpan<byte> chunkData = fileData.AsSpan(chunkFileOffset, ChunkSize);
        List<EvtxRecord> records = ReadRecords(chunkData, chunkFileOffset, (uint)chunkData.Length);
        if (records.Count == 0)
            return null;

        Dictionary<uint, BinXmlTemplateDefinition> templates = new();
        BinXmlParser binXml = new(fileData, chunkFileOffset, templates, compiledCache);

        // Render and filter — only keep records that produce non-empty output.
        // Diagnostic indices must be remapped since filtering changes the record list positions.
        (string[] xml, byte[][]? json, Dictionary<int, string> rawDiagnostics) = RenderRecords(records, binXml, format);

        List<EvtxRecord> validRecords = new List<EvtxRecord>(records.Count);
        Dictionary<int, string> remappedDiagnostics = new();
        if (json is not null)
        {
            List<byte[]> validJson = new List<byte[]>(records.Count);
            for (int i = 0; i < records.Count; i++)
            {
                if (json[i].Length > 0)
                {
                    if (rawDiagnostics.TryGetValue(i, out string? msg))
                        remappedDiagnostics[validRecords.Count] = msg;
                    validRecords.Add(records[i]);
                    validJson.Add(json[i]);
                }
            }
            if (validRecords.Count == 0)
                return null;
            return new EvtxChunk(default, templates, validRecords, Array.Empty<string>(), validJson.ToArray(), remappedDiagnostics);
        }

        List<string> validXml = new List<string>(records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            if (!string.IsNullOrEmpty(xml[i]))
            {
                if (rawDiagnostics.TryGetValue(i, out string? msg))
                    remappedDiagnostics[validRecords.Count] = msg;
                validRecords.Add(records[i]);
                validXml.Add(xml[i]);
            }
        }
        if (validRecords.Count == 0)
            return null;
        return new EvtxChunk(default, templates, validRecords, validXml.ToArray(), null, remappedDiagnostics);
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
        while (offset + 28 <= scanEnd)
        {
            ReadOnlySpan<byte> magic = chunkData.Slice(offset, 4);

            if (!magic.SequenceEqual("\x2a\x2a\x00\x00"u8))
            {
                offset += 4;
                continue;
            }

            EvtxRecord? record = EvtxRecord.ParseEvtxRecord(chunkData[offset..], chunkFileOffset + offset);
            if (record == null)
            {
                offset += 4;
                continue;
            }

            records.Add(record.Value);
            offset += (int)record.Value.Size;
        }

        return records;
    }

    /// <summary>
    /// Renders BinXml for all records, capturing diagnostics for any that fail.
    /// </summary>
    /// <param name="records">Records to render.</param>
    /// <param name="binXml">Configured BinXml parser for this chunk.</param>
    /// <param name="format">Output format (XML or JSON).</param>
    /// <returns>Rendered XML array, rendered JSON array (or null), and diagnostics for failed records.</returns>
    private static (string[] xml, byte[][]? json, Dictionary<int, string> diagnostics)
        RenderRecords(List<EvtxRecord> records, BinXmlParser binXml, OutputFormat format)
    {
        Dictionary<int, string> diagnostics = new();

        if (format == OutputFormat.Json)
        {
            byte[][] parsedJson = new byte[records.Count][];
            for (int i = 0; i < records.Count; i++)
            {
                try
                {
                    parsedJson[i] = binXml.ParseRecordJson(records[i]);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    parsedJson[i] = Array.Empty<byte>();
                    diagnostics[i] = $"BinXml render failed: {ex.Message}";
                }
            }
            return (Array.Empty<string>(), parsedJson, diagnostics);
        }

        string[] parsedXml = new string[records.Count];
        for (int i = 0; i < records.Count; i++)
        {
            try
            {
                parsedXml[i] = binXml.ParseRecord(records[i]);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                parsedXml[i] = string.Empty;
                diagnostics[i] = $"BinXml render failed: {ex.Message}";
            }
        }
        return (parsedXml, null, diagnostics);
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Size of the chunk header in bytes. Event record data begins immediately after.
    /// </summary>
    private const int _chunkHeaderSize = 512;

    #endregion
}