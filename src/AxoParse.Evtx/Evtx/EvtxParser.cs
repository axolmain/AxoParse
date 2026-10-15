using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AxoParse.Evtx.BinXml;
using AxoParse.Evtx.Wevt;

namespace AxoParse.Evtx.Evtx;

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
        // Create format-specific caches to avoid overhead for the unused format
        ConcurrentDictionary<Guid, CompiledTemplate?>? compiledCache =
            format == OutputFormat.Xml ? new ConcurrentDictionary<Guid, CompiledTemplate?>() : null;
        ConcurrentDictionary<Guid, CompiledJsonTemplate?>? compiledJsonCache =
            format == OutputFormat.Json ? new ConcurrentDictionary<Guid, CompiledJsonTemplate?>() : null;

        if (compiledCache != null)
            wevtCache?.PopulateCache(compiledCache);

        EvtxChunk[] results = new EvtxChunk[validCount];

        int parallelism = maxThreads > 0 ? maxThreads : -1;
        if (format == OutputFormat.Json)
        {
            Parallel.For(0, validCount,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                () => new Dictionary<Guid, CompiledJsonTemplate?>(), // thread local
                (i, state, localJsonCache) =>
                {
                    results[i] = EvtxChunk.Parse(
                        fileData,
                        validOffsets[i],
                        compiledCache: null,
                        compiledJsonCache: localJsonCache,
                        format);

                    return localJsonCache;
                },
                localJsonCache =>
                {
                    // merge into global cache once per thread
                    foreach (KeyValuePair<Guid, CompiledJsonTemplate?> kv in localJsonCache)
                        compiledJsonCache!.TryAdd(kv.Key, kv.Value);
                });
        }
        else
        {
            Parallel.For(0, validCount,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                () => new Dictionary<Guid, CompiledTemplate?>(), // thread local
                (i, state, localCache) =>
                {
                    results[i] = EvtxChunk.Parse(
                        fileData,
                        validOffsets[i],
                        compiledCache: localCache,
                        compiledJsonCache: null,
                        format);

                    return localCache;
                },
                localCache =>
                {
                    // merge into global cache once per thread
                    foreach (KeyValuePair<Guid, CompiledTemplate?> kv in localCache)
                        compiledCache!.TryAdd(kv.Key, kv.Value);
                });
        }

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
            if (format == OutputFormat.Json)
            {
                Parallel.For(0, invalidCount,
                    new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                    () => new Dictionary<Guid, CompiledJsonTemplate?>(),
                    (i, state, localJsonCache) =>
                    {
                        recovered[i] = EvtxChunk.ParseHeaderless(fileData, invalidOffsets[i],
                            compiledCache: null, compiledJsonCache: localJsonCache, format);
                        return localJsonCache;
                    },
                    localJsonCache =>
                    {
                        foreach (KeyValuePair<Guid, CompiledJsonTemplate?> kv in localJsonCache)
                            compiledJsonCache!.TryAdd(kv.Key, kv.Value);
                    });
            }
            else
            {
                Parallel.For(0, invalidCount,
                    new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                    () => new Dictionary<Guid, CompiledTemplate?>(),
                    (i, state, localCache) =>
                    {
                        recovered[i] = EvtxChunk.ParseHeaderless(fileData, invalidOffsets[i],
                            compiledCache: localCache, compiledJsonCache: null, format);
                        return localCache;
                    },
                    localCache =>
                    {
                        foreach (KeyValuePair<Guid, CompiledTemplate?> kv in localCache)
                            compiledCache!.TryAdd(kv.Key, kv.Value);
                    });
            }

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
    /// Parses an EVTX file and writes every record's rendered output to <paramref name="output"/> as UTF-8.
    /// Order and bytes match concatenating the output of <see cref="Parse"/>: valid chunks in file order, then records
    /// recovered from regions whose chunk header is damaged. Records are rendered into reusable per-worker buffers and
    /// written chunk by chunk, so memory use stays flat whatever the file size and no per-record output is retained.
    /// Per-record render diagnostics are not reported; a record that fails to render writes nothing.
    /// </summary>
    /// <param name="fileData">Complete EVTX file bytes.</param>
    /// <param name="output">Stream that receives the UTF-8 output. It is written to but not flushed or closed.</param>
    /// <param name="format">Output format (XML or JSON).</param>
    /// <param name="maxThreads">Thread count: 0/-1 = all cores, 1 = single-threaded, N = use N threads.</param>
    /// <param name="validateChecksums">When true, skip chunks that fail CRC32 header or data checksum validation.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>Number of records written, counted as <see cref="TotalRecords"/> counts them for the same input.</returns>
    public static int WriteTo(byte[] fileData, Stream output, OutputFormat format = OutputFormat.Xml,
                              int maxThreads = 0, bool validateChecksums = false,
                              CancellationToken cancellationToken = default)
    {
        EvtxFileHeader fileHeader = EvtxFileHeader.ParseEvtxFileHeader(fileData);
        int chunkStart = fileHeader.HeaderBlockSize;
        int chunkCount = (fileData.Length - chunkStart) / EvtxChunk.ChunkSize;

        // Same classification as Parse: magic-less regions go to recovery; checksum failures are skipped outright
        ReadOnlySpan<byte> span = fileData;
        int[] validOffsets = new int[chunkCount];
        int validCount = 0;
        int[] invalidOffsets = new int[chunkCount];
        int invalidCount = 0;
        for (int i = 0; i < chunkCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int offset = chunkStart + i * EvtxChunk.ChunkSize;
            if (offset + EvtxChunk.ChunkSize > fileData.Length)
                break;
            if (!span.Slice(offset, 8).SequenceEqual("ElfChnk\0"u8))
            {
                invalidOffsets[invalidCount++] = offset;
                continue;
            }

            if (validateChecksums)
            {
                ReadOnlySpan<byte> chunkData = span.Slice(offset, EvtxChunk.ChunkSize);
                EvtxChunkHeader header = EvtxChunkHeader.ParseEvtxChunkHeader(chunkData);
                if (!header.ValidateHeaderChecksum(chunkData) || !header.ValidateDataChecksum(chunkData))
                    continue;
            }

            validOffsets[validCount++] = offset;
        }

        int workers = maxThreads > 0 ? maxThreads : Environment.ProcessorCount;
        int total = WriteChunks(fileData, validOffsets, validCount, headerless: false, format, workers, output, cancellationToken);
        total += WriteChunks(fileData, invalidOffsets, invalidCount, headerless: true, format, workers, output, cancellationToken);
        return total;
    }

    /// <summary>
    /// Parses an EVTX file and streams events as chunks complete, without waiting for the entire file.
    /// Chunks are parsed in parallel and yielded in file order via a reorder buffer.
    /// Same parsing logic as <see cref="Parse"/> but events are available incrementally.
    /// </summary>
    /// <param name="fileData">Complete EVTX file bytes.</param>
    /// <param name="maxThreads">Thread count: 0/-1 = all cores, 1 = single-threaded, N = use N threads.</param>
    /// <param name="format">Output format (XML or JSON).</param>
    /// <param name="validateChecksums">When true, skip chunks that fail CRC32 header or data checksum validation.</param>
    /// <param name="wevtCache">Optional offline template cache built from provider PE binaries.</param>
    /// <param name="cancellationToken">Token to cancel the parse operation.</param>
    /// <returns>Events streamed in file order as chunks finish parsing.</returns>
    public static async IAsyncEnumerable<EvtxEvent> ParseAsync(
        byte[] fileData, int maxThreads = 0, OutputFormat format = OutputFormat.Xml,
        bool validateChecksums = false, WevtCache? wevtCache = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EvtxFileHeader fileHeader = EvtxFileHeader.ParseEvtxFileHeader(fileData);
        int chunkStart = fileHeader.HeaderBlockSize;
        int chunkCount = (fileData.Length - chunkStart) / EvtxChunk.ChunkSize;

        // Phase 1 (sequential): validate chunks, collect valid/invalid offsets
        ReadOnlySpan<byte> span = fileData;
        int[] validOffsets = new int[chunkCount];
        int validCount = 0;
        int[] invalidOffsets = new int[chunkCount];
        int invalidCount = 0;

        for (int i = 0; i < chunkCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int offset = chunkStart + i * EvtxChunk.ChunkSize;
            if (offset + EvtxChunk.ChunkSize > fileData.Length)
                break;

            if (!span.Slice(offset, 8).SequenceEqual("ElfChnk\0"u8))
            {
                invalidOffsets[invalidCount++] = offset;
                continue;
            }

            if (validateChecksums)
            {
                ReadOnlySpan<byte> chunkData = span.Slice(offset, EvtxChunk.ChunkSize);
                EvtxChunkHeader header = EvtxChunkHeader.ParseEvtxChunkHeader(chunkData);
                if (!header.ValidateHeaderChecksum(chunkData) || !header.ValidateDataChecksum(chunkData))
                {
                    invalidOffsets[invalidCount++] = offset;
                    continue;
                }
            }

            validOffsets[validCount++] = offset;
        }

        // Setup format-specific caches
        ConcurrentDictionary<Guid, CompiledTemplate?>? compiledCache =
            format == OutputFormat.Xml ? new ConcurrentDictionary<Guid, CompiledTemplate?>() : null;
        ConcurrentDictionary<Guid, CompiledJsonTemplate?>? compiledJsonCache =
            format == OutputFormat.Json ? new ConcurrentDictionary<Guid, CompiledJsonTemplate?>() : null;

        if (compiledCache != null)
            wevtCache?.PopulateCache(compiledCache);

        int parallelism = maxThreads > 0 ? maxThreads : -1;
        // Unbounded channel: chunk count is finite (file size / 64KB) so memory is bounded by the file itself
        Channel<(int Index, EvtxChunk Chunk)> channel = Channel.CreateUnbounded<(int, EvtxChunk)>(
            new UnboundedChannelOptions { SingleReader = true });

        // Capture counts for the producer closure
        int validCountCopy = validCount;
        int invalidCountCopy = invalidCount;

        Task producer = Task.Run(() =>
        {
            try
            {
                // Phase 2: parallel parse valid chunks
                if (format == OutputFormat.Json)
                {
                    Parallel.For(0, validCountCopy,
                        new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                        () => new Dictionary<Guid, CompiledJsonTemplate?>(),
                        (i, state, localJsonCache) =>
                        {
                            EvtxChunk chunk = EvtxChunk.Parse(fileData, validOffsets[i],
                                compiledCache: null, compiledJsonCache: localJsonCache, format);
                            channel.Writer.TryWrite((i, chunk));
                            return localJsonCache;
                        },
                        localJsonCache =>
                        {
                            foreach (KeyValuePair<Guid, CompiledJsonTemplate?> kv in localJsonCache)
                                compiledJsonCache!.TryAdd(kv.Key, kv.Value);
                        });
                }
                else
                {
                    Parallel.For(0, validCountCopy,
                        new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                        () => new Dictionary<Guid, CompiledTemplate?>(),
                        (i, state, localCache) =>
                        {
                            EvtxChunk chunk = EvtxChunk.Parse(fileData, validOffsets[i],
                                compiledCache: localCache, compiledJsonCache: null, format);
                            channel.Writer.TryWrite((i, chunk));
                            return localCache;
                        },
                        localCache =>
                        {
                            foreach (KeyValuePair<Guid, CompiledTemplate?> kv in localCache)
                                compiledCache!.TryAdd(kv.Key, kv.Value);
                        });
                }

                // Phase 4: parallel recovery of headerless chunks (indices offset by validCount)
                if (invalidCountCopy > 0)
                {
                    if (format == OutputFormat.Json)
                    {
                        Parallel.For(0, invalidCountCopy,
                            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                            () => new Dictionary<Guid, CompiledJsonTemplate?>(),
                            (i, state, localJsonCache) =>
                            {
                                EvtxChunk? recovered = EvtxChunk.ParseHeaderless(fileData, invalidOffsets[i],
                                    compiledCache: null, compiledJsonCache: localJsonCache, format);
                                if (recovered != null)
                                    channel.Writer.TryWrite((validCountCopy + i, recovered));
                                return localJsonCache;
                            },
                            localJsonCache =>
                            {
                                foreach (KeyValuePair<Guid, CompiledJsonTemplate?> kv in localJsonCache)
                                    compiledJsonCache!.TryAdd(kv.Key, kv.Value);
                            });
                    }
                    else
                    {
                        Parallel.For(0, invalidCountCopy,
                            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                            () => new Dictionary<Guid, CompiledTemplate?>(),
                            (i, state, localCache) =>
                            {
                                EvtxChunk? recovered = EvtxChunk.ParseHeaderless(fileData, invalidOffsets[i],
                                    compiledCache: localCache, compiledJsonCache: null, format);
                                if (recovered != null)
                                    channel.Writer.TryWrite((validCountCopy + i, recovered));
                                return localCache;
                            },
                            localCache =>
                            {
                                foreach (KeyValuePair<Guid, CompiledTemplate?> kv in localCache)
                                    compiledCache!.TryAdd(kv.Key, kv.Value);
                            });
                    }
                }
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, cancellationToken);

        // Consumer: reorder buffer ensures events are yielded in file order
        Dictionary<int, EvtxChunk> reorderBuffer = new();
        int nextExpected = 0;

        await foreach ((int index, EvtxChunk chunk) in channel.Reader.ReadAllAsync(cancellationToken))
        {
            reorderBuffer[index] = chunk;

            while (reorderBuffer.Remove(nextExpected, out EvtxChunk? ready))
            {
                for (int i = 0; i < ready.Records.Count; i++)
                    yield return ready.GetEvent(i);
                nextExpected++;
            }
        }

        // Flush any remaining buffered chunks (recovery chunks may have gaps from null results)
        while (reorderBuffer.Count > 0)
        {
            if (reorderBuffer.Remove(nextExpected, out EvtxChunk? ready))
            {
                for (int i = 0; i < ready.Records.Count; i++)
                    yield return ready.GetEvent(i);
            }
            nextExpected++;
        }

        await producer;
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

    #region Non-Public Methods

    /// <summary>
    /// Renders the given chunks and writes their UTF-8 output to <paramref name="output"/> in offset-array order.
    /// With several workers, chunks are rendered in parallel one window at a time and each window is written in order.
    /// Each call starts with empty template caches, as each phase of <see cref="Parse"/> does, so recovered regions
    /// never see templates compiled for valid chunks.
    /// </summary>
    /// <param name="fileData">Complete EVTX file bytes.</param>
    /// <param name="offsets">Absolute file offsets of the chunks to render.</param>
    /// <param name="count">Number of entries of <paramref name="offsets"/> to use.</param>
    /// <param name="headerless">True when the chunks are recovery candidates with damaged or missing headers.</param>
    /// <param name="format">Output format (XML or JSON).</param>
    /// <param name="workers">Number of rendering threads (1 = render on the calling thread).</param>
    /// <param name="output">Stream that receives the UTF-8 output.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>Number of records written, counted as <see cref="EvtxChunk.RenderTo"/> counts them.</returns>
    private static int WriteChunks(byte[] fileData, int[] offsets, int count, bool headerless, OutputFormat format,
                                   int workers, Stream output, CancellationToken cancellationToken)
    {
        if (count == 0)
            return 0;

        bool xml = format == OutputFormat.Xml;
        int total = 0;
        if (workers == 1)
        {
            Dictionary<Guid, CompiledTemplate?>? xmlCache = xml ? new Dictionary<Guid, CompiledTemplate?>() : null;
            Dictionary<Guid, CompiledJsonTemplate?>? jsonCache = xml ? null : new Dictionary<Guid, CompiledJsonTemplate?>();
            ArrayBufferWriter<byte> sink = new(_initialSinkBytes);
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sink.ResetWrittenCount();
                total += EvtxChunk.RenderTo(fileData, offsets[i], headerless, xmlCache, jsonCache, format, sink);
                output.Write(sink.WrittenSpan);
            }

            return total;
        }

        // Template caches persist per thread across windows, like the thread-local caches of Parse
        using ThreadLocal<Dictionary<Guid, CompiledTemplate?>?> xmlCaches =
            new(() => xml ? new Dictionary<Guid, CompiledTemplate?>() : null);
        using ThreadLocal<Dictionary<Guid, CompiledJsonTemplate?>?> jsonCaches =
            new(() => xml ? null : new Dictionary<Guid, CompiledJsonTemplate?>());
        int window = workers * _chunksPerWorkerPerWindow;
        ArrayBufferWriter<byte>?[] sinks = new ArrayBufferWriter<byte>?[window];
        int[] kept = new int[window];
        ParallelOptions options = new() { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken };
        for (int start = 0; start < count; start += window)
        {
            int windowStart = start;
            int length = Math.Min(window, count - start);
            Parallel.For(0, length, options, k =>
            {
                ArrayBufferWriter<byte> sink = sinks[k] ??= new ArrayBufferWriter<byte>(_initialSinkBytes);
                sink.ResetWrittenCount();
                kept[k] = EvtxChunk.RenderTo(fileData, offsets[windowStart + k], headerless,
                    xmlCaches.Value, jsonCaches.Value, format, sink);
            });

            for (int k = 0; k < length; k++)
            {
                output.Write(sinks[k]!.WrittenSpan);
                total += kept[k];
            }
        }

        return total;
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Initial size of each per-worker UTF-8 output buffer used by <see cref="WriteTo"/> (128 KB).
    /// A 64 KB chunk typically renders to 100-200 KB of text; buffers grow on demand and are reused.
    /// </summary>
    private const int _initialSinkBytes = 128 * 1024;

    /// <summary>
    /// Chunks rendered per worker before <see cref="WriteTo"/> writes a window out in order. Larger windows smooth
    /// out uneven chunk costs; smaller ones bound memory to window × chunk output size.
    /// </summary>
    private const int _chunksPerWorkerPerWindow = 2;

    #endregion
}