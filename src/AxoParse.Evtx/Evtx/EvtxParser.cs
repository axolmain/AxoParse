using System.Collections.Concurrent;

namespace AxoParse.Evtx;

/// <summary>
/// Top-level orchestrator. Parses the file header, slices chunks, and collects all parsed data.
/// </summary>
public class EvtxParser
{
    #region Constructors And Destructors

    /// <summary>
    /// Constructs an EvtxParser result from pre-parsed components.
    /// </summary>
    /// <param name="rawData">Complete EVTX file bytes.</param>
    /// <param name="fileHeader">Parsed file header.</param>
    /// <param name="chunks">Parsed chunks.</param>
    /// <param name="totalRecords">Aggregate record count.</param>
    /// <param name="diagnostics">Parser-level diagnostic messages.</param>
    private EvtxParser(byte[] rawData, EvtxFileHeader fileHeader, List<EvtxChunk> chunks,
                       int totalRecords, List<string> diagnostics)
    {
        RawData = rawData;
        FileHeader = fileHeader;
        Chunks = chunks;
        TotalRecords = totalRecords;
        Diagnostics = diagnostics;
    }

    #endregion

    #region Properties

    /// <summary>
    /// All successfully parsed 64KB chunks from the file, in file order.
    /// </summary>
    public IReadOnlyList<EvtxChunk> Chunks { get; }

    /// <summary>
    /// Parser-level diagnostic messages (e.g. chunks skipped due to invalid checksums).
    /// Empty if no issues were encountered during parsing.
    /// </summary>
    public IReadOnlyList<string> Diagnostics { get; }

    /// <summary>
    /// Parsed EVTX file header (first 4096 bytes) containing version info, chunk count, and flags.
    /// </summary>
    public EvtxFileHeader FileHeader { get; }

    /// <summary>
    /// The complete EVTX file bytes. Retained so parsed records can lazily reference event data via spans.
    /// </summary>
    public ReadOnlyMemory<byte> RawData { get; }

    /// <summary>
    /// Total number of event records across all parsed chunks.
    /// </summary>
    public int TotalRecords { get; }

    #endregion

    #region Public Methods

    /// <summary>
    /// Parses an entire EVTX file from a byte array.
    /// </summary>
    /// <param name="fileData">Complete EVTX file bytes.</param>
    /// <param name="maxThreads">Thread count: 0/-1 = all cores, 1 = single-threaded, N = use N threads.</param>
    /// <param name="format">Output format (XML or JSON).</param>
    /// <param name="validateChecksums">When true, skip chunks that fail CRC32 header or data checksum validation.</param>
    /// <param name="wevtCache">Optional offline template cache built from provider PE binaries. Pre-populates the compiled template cache before chunk parsing.</param>
    /// <param name="cancellationToken">Token to cancel the parse operation.</param>
    /// <returns>A fully parsed <see cref="EvtxParser"/> containing the file header, chunks, and aggregate record count.</returns>
    public static EvtxParser Parse(byte[] fileData, int maxThreads = 0, OutputFormat format = OutputFormat.Xml,
                                   bool validateChecksums = false, WevtCache? wevtCache = null,
                                   CancellationToken cancellationToken = default)
    {
        EvtxFileHeader fileHeader = EvtxFileHeader.ParseEvtxFileHeader(fileData);
        int chunkStart = fileHeader.HeaderBlockSize;

        // Compute chunk count from file size
        int chunkCount = (fileData.Length - chunkStart) / EvtxChunk.ChunkSize;

        // Phase 1 (sequential): scan chunks, validate magic + optional checksums, collect valid offsets
        ReadOnlySpan<byte> span = fileData;
        int[] validOffsets = new int[chunkCount];
        int validCount = 0;
        List<string> diagnostics = new();

        for (int i = 0; i < chunkCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
                {
                    diagnostics.Add($"Chunk at offset 0x{offset:X} skipped: CRC32 checksum validation failed");
                    continue;
                }
            }

            validOffsets[validCount++] = offset;
        }

        // Phase 2 (parallel): parse all valid chunks
        ConcurrentDictionary<Guid, CompiledTemplate?> compiledCache = new();
        wevtCache?.PopulateCache(compiledCache);
        EvtxChunk[] results = new EvtxChunk[validCount];

        int parallelism = maxThreads > 0 ? maxThreads : -1;
        Parallel.For(0, validCount,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            () => new Dictionary<Guid, CompiledTemplate?>(), // thread local
            (i, state, localCache) =>
            {
                results[i] = EvtxChunk.Parse(
                    fileData,
                    validOffsets[i],
                    localCache,
                    format);

                return localCache;
            },
            localCache =>
            {
                // merge into global cache once per thread
                foreach (KeyValuePair<Guid, CompiledTemplate?> kv in localCache)
                    compiledCache.TryAdd(kv.Key, kv.Value);
            });

        // Phase 3 (sequential): collect results from valid chunks
        cancellationToken.ThrowIfCancellationRequested();
        List<EvtxChunk> chunks = new List<EvtxChunk>(validCount);
        int totalRecords = 0;
        for (int i = 0; i < validCount; i++)
        {
            chunks.Add(results[i]);
            totalRecords += results[i].Records.Count;
        }

        // Phase 4 (parallel): attempt recovery on chunks with bad/missing headers
        int[] invalidOffsets = new int[chunkCount - validCount];
        int invalidCount = 0;
        for (int i = 0; i < chunkCount; i++)
        {
            int offset = chunkStart + i * EvtxChunk.ChunkSize;
            if (offset + EvtxChunk.ChunkSize > fileData.Length)
                break;
            if (span.Slice(offset, 8).SequenceEqual("ElfChnk\0"u8))
                continue;
            invalidOffsets[invalidCount++] = offset;
        }

        if (invalidCount > 0)
        {
            EvtxChunk?[] recovered = new EvtxChunk?[invalidCount];
            Parallel.For(0, invalidCount,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                () => new Dictionary<Guid, CompiledTemplate?>(),
                (i, state, localCache) =>
                {
                    recovered[i] = EvtxChunk.ParseHeaderless(fileData, invalidOffsets[i], localCache, format);
                    return localCache;
                },
                localCache =>
                {
                    foreach (KeyValuePair<Guid, CompiledTemplate?> kv in localCache)
                        compiledCache.TryAdd(kv.Key, kv.Value);
                });

            for (int i = 0; i < invalidCount; i++)
            {
                if (recovered[i] != null)
                {
                    chunks.Add(recovered[i]!);
                    totalRecords += recovered[i]!.Records.Count;
                }
            }
        }

        return new EvtxParser(fileData, fileHeader, chunks, totalRecords, diagnostics);
    }

    /// <summary>
    /// Enumerates all parsed events across all chunks in file order.
    /// Each event pairs record metadata with rendered output and optional diagnostic info.
    /// </summary>
    /// <returns>All parsed events flattened across chunks.</returns>
    public IEnumerable<EvtxEvent> GetEvents()
    {
        foreach (EvtxChunk chunk in Chunks)
        {
            for (int i = 0; i < chunk.Records.Count; i++)
            {
                yield return chunk.GetEvent(i);
            }
        }
    }

    #endregion
}