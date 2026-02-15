using System.Collections.Concurrent;
using System.Runtime.InteropServices;

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
/// The file body consists of sequentially laid-out chunks, each exactly 64 KB. Each chunk is a self-contained unit with
/// its own header, a sequence of event records encoded in Binary XML, and trailing unused space. Chunks maintain their
/// own string and template caches for deduplication within the chunk boundary. The chunk header includes CRC32
/// checksums for both the header and the event record data area.
/// </summary>
public class EvtxChunk
{
    /// <summary>
    /// Size of each chunk in bytes (64 KB).
    /// </summary>
    public const int ChunkSize = 65536;

    /// <summary>
    /// Size of the chunk header in bytes. Event record data begins immediately after.
    /// </summary>
    private const int ChunkHeaderSize = 512;

    /// <summary>
    /// Parsed chunk header containing record ranges, offsets, checksums, and flags.
    /// </summary>
    public EvtxChunkHeader Header { get; }

    /// <summary>
    /// Template definitions preloaded from this chunk's 32-entry template pointer table,
    /// keyed by chunk-relative offset.
    /// </summary>
    public Dictionary<uint, BinXmlTemplateDefinition> Templates { get; }

    /// <summary>
    /// Event records parsed from this chunk's data area.
    /// </summary>
    public List<EvtxRecord> Records { get; }

    /// <summary>
    /// BinXml-rendered XML strings, one per record, in the same order as <see cref="Records"/>.
    /// Empty when output format is JSON.
    /// </summary>
    public string[] ParsedXml { get; }

    /// <summary>
    /// BinXml-rendered UTF-8 JSON byte arrays, one per record, in the same order as <see cref="Records"/>.
    /// Null when output format is XML.
    /// </summary>
    public byte[][]? ParsedJson { get; }

    /// <summary>
    /// Constructs an EvtxChunk from pre-parsed components.
    /// </summary>
    /// <param name="header">Parsed chunk header.</param>
    /// <param name="templates">Template definitions keyed by chunk-relative offset.</param>
    /// <param name="records">Parsed event records.</param>
    /// <param name="parsedXml">Rendered XML strings (empty array when using JSON output).</param>
    /// <param name="parsedJson">Rendered JSON byte arrays, or null when using XML output.</param>
    private EvtxChunk(EvtxChunkHeader header, Dictionary<uint, BinXmlTemplateDefinition> templates,
                      List<EvtxRecord> records, string[] parsedXml, byte[][]? parsedJson = null)
    {
        Header = header;
        Templates = templates;
        Records = records;
        ParsedXml = parsedXml;
        ParsedJson = parsedJson;
    }

    /// <summary>
    /// Walks event records within a chunk's data area, resilient to corrupted records.
    /// Scans from the end of the 512-byte chunk header up to the free-space boundary,
    /// skipping non-record regions (4-byte aligned scan) and records that fail size/integrity
    /// validation. Stops cleanly on zero-filled magic (some producers write bad free_space_offset).
    /// </summary>
    /// <param name="chunkData">Full 64KB chunk span.</param>
    /// <param name="header">Parsed chunk header (provides record count estimate and free-space offset).</param>
    /// <param name="chunkFileOffset">Absolute file offset of this chunk.</param>
    /// <returns>List of successfully parsed records.</returns>
    private static List<EvtxRecord> ReadRecords(ReadOnlySpan<byte> chunkData, EvtxChunkHeader header, int chunkFileOffset)
    {
        uint freeSpaceEnd = Math.Min(header.FreeSpaceOffset, (uint)chunkData.Length);
        int expectedRecords = (int)(header.LastEventRecordId - header.FirstEventRecordId + 1);
        List<EvtxRecord> records = new List<EvtxRecord>(expectedRecords);

        int offset = ChunkHeaderSize;
        while (offset + 28 <= freeSpaceEnd)
        {
            ReadOnlySpan<byte> magic = chunkData.Slice(offset, 4);

            // Zero magic = clean end-of-chunk (some producers write bad free_space_offset)
            if (magic[0] == 0 && magic[1] == 0 && magic[2] == 0 && magic[3] == 0)
                break;

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
    /// Parses a 64KB chunk: header, preloads templates, walks event records, and parses BinXml.
    /// </summary>
    /// <param name="chunkData">Exactly 64KB span covering the chunk.</param>
    /// <param name="chunkFileOffset">Absolute byte offset of this chunk within the EVTX file.</param>
    /// <param name="fileData">Complete EVTX file bytes (needed by BinXml parser for cross-chunk template references).</param>
    /// <param name="compiledCache">Thread-safe cache of compiled templates shared across chunks.</param>
    /// <param name="format">Output format for rendered event records.</param>
    /// <returns>A fully parsed <see cref="EvtxChunk"/> with rendered output.</returns>
    internal static EvtxChunk Parse(ReadOnlySpan<byte> chunkData, int chunkFileOffset,
                                    byte[] fileData, ConcurrentDictionary<Guid, CompiledTemplate?> compiledCache,
                                    OutputFormat format = OutputFormat.Xml)
    {
        EvtxChunkHeader header = EvtxChunkHeader.ParseEvtxChunkHeader(chunkData);

        // Read template ptrs inline from the chunk span — no array allocation
        Dictionary<uint, BinXmlTemplateDefinition> templates =
            BinXmlTemplateDefinition.PreloadFromChunk(chunkData,
                MemoryMarshal.Cast<byte, uint>(chunkData.Slice(384, 128)), chunkFileOffset);

        List<EvtxRecord> records = ReadRecords(chunkData, header, chunkFileOffset);

        // Parse BinXml for each record
        BinXmlParser binXml = new(fileData, chunkFileOffset, templates, compiledCache);

        if (format == OutputFormat.Json)
        {
            byte[][] parsedJson = new byte[records.Count][];
            for (int i = 0; i < records.Count; i++)
                parsedJson[i] = binXml.ParseRecordJson(records[i]);
            return new EvtxChunk(header, templates, records, Array.Empty<string>(), parsedJson);
        }

        string[] parsedXml = new string[records.Count];
        for (int i = 0; i < records.Count; i++)
            parsedXml[i] = binXml.ParseRecord(records[i]);
        return new EvtxChunk(header, templates, records, parsedXml);
    }

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

        List<EvtxRecord> records = ReadRecords(chunkData, header, chunkFileOffset);

        return new EvtxChunk(header, templates, records, Array.Empty<string>());
    }

    /// <summary>
    /// Parses a 64KB chunk from a byte[] — safe for use from Parallel.For lambdas
    /// (ReadOnlySpan is a ref struct and cannot cross thread boundaries).
    /// </summary>
    /// <param name="fileData">Complete EVTX file bytes.</param>
    /// <param name="chunkFileOffset">Absolute byte offset of this chunk within the EVTX file.</param>
    /// <param name="compiledCache">Thread-safe cache of compiled templates shared across chunks.</param>
    /// <param name="format">Output format for rendered event records.</param>
    /// <returns>A fully parsed <see cref="EvtxChunk"/> with rendered output.</returns>
    internal static EvtxChunk Parse(byte[] fileData, int chunkFileOffset,
                                    ConcurrentDictionary<Guid, CompiledTemplate?> compiledCache,
                                    OutputFormat format = OutputFormat.Xml)
    {
        ReadOnlySpan<byte> chunkData = fileData.AsSpan(chunkFileOffset, ChunkSize);
        return Parse(chunkData, chunkFileOffset, fileData, compiledCache, format);
    }
}