using System.Collections.Concurrent;

namespace AxoParse.Evtx;

/// <summary>
/// Specifies the output format for parsed EVTX event records.
/// </summary>
public enum OutputFormat
{
    /// <summary>
    /// Render each event record as an XML string.
    /// </summary>
    Xml,

    /// <summary>
    /// Render each event record as a UTF-8 JSON byte array.
    /// </summary>
    Json
}

/// <summary>
/// Top-level orchestrator. Parses the file header, slices chunks, and collects all parsed data.
/// </summary>
public class EvtxParser
{
    /// <summary>
    /// The complete EVTX file bytes. Retained so parsed records can lazily reference event data via spans.
    /// </summary>
    public byte[] RawData { get; }

    /// <summary>
    /// Parsed EVTX file header (first 4096 bytes) containing version info, chunk count, and flags.
    /// </summary>
    public EvtxFileHeader FileHeader { get; }

    /// <summary>
    /// All successfully parsed 64KB chunks from the file, in file order.
    /// </summary>
    public List<EvtxChunk> Chunks { get; }

    /// <summary>
    /// Total number of event records across all parsed chunks.
    /// </summary>
    public int TotalRecords { get; }

    /// <summary>
    /// Constructs an EvtxParser result from pre-parsed components.
    /// </summary>
    /// <param name="rawData">Complete EVTX file bytes.</param>
    /// <param name="fileHeader">Parsed file header.</param>
    /// <param name="chunks">Parsed chunks.</param>
    /// <param name="totalRecords">Aggregate record count.</param>
    private EvtxParser(byte[] rawData, EvtxFileHeader fileHeader, List<EvtxChunk> chunks, int totalRecords)
    {
        RawData = rawData;
        FileHeader = fileHeader;
        Chunks = chunks;
        TotalRecords = totalRecords;
    }

    /// <summary>
    /// Parses an entire EVTX file from a byte array.
    /// maxThreads: 0 or -1 = all cores, 1 = single-threaded, N = use N threads.
    /// </summary>
    /// <param name="fileData">Complete EVTX file bytes.</param>
    /// <param name="maxThreads">Thread count: 0/-1 = all cores, 1 = single-threaded, N = use N threads.</param>
    /// <param name="format">Output format (XML or JSON).</param>
    /// <param name="validateChecksums">When true, skip chunks that fail CRC32 header or data checksum validation.</param>
    /// <param name="wevtCache">Optional offline template cache built from provider PE binaries. Pre-populates the compiled template cache before chunk parsing.</param>
    /// <returns>A fully parsed <see cref="EvtxParser"/> containing the file header, chunks, and aggregate record count.</returns>
    public static EvtxParser Parse(byte[] fileData, int maxThreads = 0, OutputFormat format = OutputFormat.Xml,
                                   bool validateChecksums = false, WevtCache? wevtCache = null)
    {
        EvtxFileHeader fileHeader = EvtxFileHeader.ParseEvtxFileHeader(fileData);
        int chunkStart = fileHeader.HeaderBlockSize;

        // Compute chunk count from file size
        int chunkCount = (fileData.Length - chunkStart) / EvtxChunk.ChunkSize;

        // Phase 1 (sequential): scan chunks, validate magic + optional checksums, collect valid offsets
        ReadOnlySpan<byte> span = fileData;
        int[] validOffsets = new int[chunkCount];
        int validCount = 0;

        for (int i = 0; i < chunkCount; i++)
        {
            int offset = chunkStart + i * EvtxChunk.ChunkSize;
            if (offset + EvtxChunk.ChunkSize > fileData.Length)
                break;
            if (!span.Slice(offset, 8).SequenceEqual("ElfChnk\0"u8))
                continue;

            if (validateChecksums)
            {
                ReadOnlySpan<byte> chunkData = span.Slice(offset, EvtxChunk.ChunkSize);
                EvtxChunkHeader header = EvtxChunkHeader.ParseEvtxChunkHeader(chunkData);
                if (!header.ValidateHeaderChecksum(chunkData) || !header.ValidateDataChecksum(chunkData))
                    continue;
            }

            validOffsets[validCount++] = offset;
        }

        // Phase 2 (parallel): parse all valid chunks
        ConcurrentDictionary<Guid, CompiledTemplate?> compiledCache = new();
        wevtCache?.PopulateCache(compiledCache);
        EvtxChunk[] results = new EvtxChunk[validCount];

        int parallelism = maxThreads > 0 ? maxThreads : -1;
        Parallel.For(0, validCount,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            i =>
            {
                results[i] = EvtxChunk.Parse(fileData, validOffsets[i], compiledCache, format);
            });

        // Phase 3 (sequential): collect results
        List<EvtxChunk> chunks = new List<EvtxChunk>(validCount);
        int totalRecords = 0;
        for (int i = 0; i < validCount; i++)
        {
            chunks.Add(results[i]);
            totalRecords += results[i].Records.Count;
        }

        return new EvtxParser(fileData, fileHeader, chunks, totalRecords);
    }
}