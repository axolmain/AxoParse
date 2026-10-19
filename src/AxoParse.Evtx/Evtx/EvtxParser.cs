using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AxoParse.Evtx.BinXml;
using AxoParse.Evtx.Wevt;
using Microsoft.Win32.SafeHandles;

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
    /// <param name="wevtCache">Optional provider (WEVT) templates, used only for records whose embedded template is missing, runs
    /// past its chunk or won't compile; a usable embedded template is never replaced.</param>
    /// <param name="cancellationToken">Token to cancel the parse operation.</param>
    /// <param name="salvage">When true, a record whose BinXml is corrupt keeps the well-formed output decoded before the
    /// corruption (open elements are closed) instead of failing with empty output; its <see cref="EvtxEvent.Diagnostic"/>
    /// still says where decoding stopped. Off by default: corrupt records fail, as in the Rust evtx crate.</param>
    /// <returns>A fully parsed <see cref="EvtxParser"/> containing the file header, chunks, and aggregate record count.</returns>
    public static EvtxParser Parse(byte[] fileData, int maxThreads = 0, OutputFormat format = OutputFormat.Xml,
                                   bool validateChecksums = false, WevtCache? wevtCache = null,
                                   CancellationToken cancellationToken = default, bool salvage = false)
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

        // Phase 2 (parallel): parse all valid chunks. Each worker keeps its own compiled-template cache.

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
                    compiledCache: localCache,
                    format,
                    wevtCache,
                    salvage);

                return localCache;
            },
            _ => { });

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
                    recovered[i] = EvtxChunk.ParseHeaderless(fileData, invalidOffsets[i],
                        compiledCache: localCache, format, wevtCache, salvage);
                    return localCache;
                },
                _ => { });

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
    /// <param name="wevtCache">Optional provider (WEVT) templates for records whose embedded template is missing, runs past its
    /// chunk or won't compile; a usable embedded template is never replaced. Last so existing positional calls still compile.</param>
    /// <param name="salvage">When true, a record whose BinXml is corrupt writes the well-formed output decoded before the
    /// corruption (open elements are closed) instead of nothing. Off by default.</param>
    /// <returns>Number of records written, counted as <see cref="TotalRecords"/> counts them for the same input.</returns>
    public static int WriteTo(byte[] fileData, Stream output, OutputFormat format = OutputFormat.Xml,
                              int maxThreads = 0, bool validateChecksums = false,
                              CancellationToken cancellationToken = default, WevtCache? wevtCache = null,
                              bool salvage = false)
    {
        EvtxFileHeader fileHeader = EvtxFileHeader.ParseEvtxFileHeader(fileData);
        return WriteAll(new ChunkSource(fileData, null), fileHeader.HeaderBlockSize, fileData.Length, output, format,
            maxThreads, validateChecksums, wevtCache, salvage, cancellationToken);
    }

    /// <summary>
    /// Streams an EVTX file from disk and writes every record's rendered output to <paramref name="output"/> as UTF-8,
    /// without loading the file into memory. Each worker reads the 64 KB chunk it is about to render, so reading
    /// overlaps rendering and memory stays at a few buffers per worker whatever the file size (files over 2 GB work).
    /// Output and return value are identical to <see cref="WriteTo(byte[], Stream, OutputFormat, int, bool, CancellationToken, WevtCache?, bool)"/>
    /// for the same bytes. The file is opened for reading with <see cref="FileShare.ReadWrite"/>, so a log another
    /// process still has open can be read.
    /// </summary>
    /// <param name="path">Path of the .evtx file.</param>
    /// <param name="output">Stream that receives the UTF-8 output. It is written to but not flushed or closed.</param>
    /// <param name="format">Output format (XML or JSON).</param>
    /// <param name="maxThreads">Thread count: 0/-1 = all cores, 1 = single-threaded, N = use N threads.</param>
    /// <param name="validateChecksums">When true, skip chunks that fail CRC32 header or data checksum validation.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <param name="wevtCache">Optional provider (WEVT) templates for records whose embedded template is missing, runs past its
    /// chunk or won't compile; a usable embedded template is never replaced. Last so existing positional calls still compile.</param>
    /// <param name="salvage">When true, a record whose BinXml is corrupt writes the well-formed output decoded before the
    /// corruption (open elements are closed) instead of nothing. Off by default.</param>
    /// <returns>Number of records written, counted as <see cref="TotalRecords"/> counts them for the same input.</returns>
    public static int WriteTo(string path, Stream output, OutputFormat format = OutputFormat.Xml,
                              int maxThreads = 0, bool validateChecksums = false,
                              CancellationToken cancellationToken = default, WevtCache? wevtCache = null,
                              bool salvage = false)
    {
        using SafeFileHandle file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return WriteTo(file, output, format, maxThreads, validateChecksums, cancellationToken, wevtCache, salvage);
    }

    /// <summary>
    /// Streams an EVTX file from an open handle; see <see cref="WriteTo(string, Stream, OutputFormat, int, bool, CancellationToken, WevtCache?, bool)"/>.
    /// Chunks are read with positional reads (<see cref="RandomAccess"/>), so the handle's file position is not used
    /// or changed and the handle stays open.
    /// </summary>
    /// <param name="file">Handle opened for reading, e.g. by <see cref="File.OpenHandle"/>.</param>
    /// <param name="output">Stream that receives the UTF-8 output. It is written to but not flushed or closed.</param>
    /// <param name="format">Output format (XML or JSON).</param>
    /// <param name="maxThreads">Thread count: 0/-1 = all cores, 1 = single-threaded, N = use N threads.</param>
    /// <param name="validateChecksums">When true, skip chunks that fail CRC32 header or data checksum validation.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <param name="wevtCache">Optional provider (WEVT) templates for records whose embedded template is missing, runs past its
    /// chunk or won't compile; a usable embedded template is never replaced. Last so existing positional calls still compile.</param>
    /// <param name="salvage">When true, a record whose BinXml is corrupt writes the well-formed output decoded before the
    /// corruption (open elements are closed) instead of nothing. Off by default.</param>
    /// <returns>Number of records written, counted as <see cref="TotalRecords"/> counts them for the same input.</returns>
    public static int WriteTo(SafeFileHandle file, Stream output, OutputFormat format = OutputFormat.Xml,
                              int maxThreads = 0, bool validateChecksums = false,
                              CancellationToken cancellationToken = default, WevtCache? wevtCache = null,
                              bool salvage = false)
    {
        long length = RandomAccess.GetLength(file);
        Span<byte> headerBytes = stackalloc byte[_fileHeaderReadBytes];
        int headerLength = ChunkSource.ReadFully(file, headerBytes[..(int)Math.Min(length, _fileHeaderReadBytes)], 0);
        EvtxFileHeader fileHeader = EvtxFileHeader.ParseEvtxFileHeader(headerBytes[..headerLength]);
        return WriteAll(new ChunkSource(null, file), fileHeader.HeaderBlockSize, length, output, format, maxThreads,
            validateChecksums, wevtCache, salvage, cancellationToken);
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
    /// <param name="wevtCache">Optional provider (WEVT) templates for records whose embedded template is missing or corrupt (see
    /// <see cref="Parse"/>).</param>
    /// <param name="cancellationToken">Token to cancel the parse operation.</param>
    /// <param name="salvage">When true, a record whose BinXml is corrupt keeps the well-formed output decoded before the
    /// corruption (open elements are closed) instead of failing with empty output; its <see cref="EvtxEvent.Diagnostic"/>
    /// still says where decoding stopped. Off by default: corrupt records fail, as in the Rust evtx crate.</param>
    /// <returns>Events streamed in file order as chunks finish parsing.</returns>
    public static async IAsyncEnumerable<EvtxEvent> ParseAsync(
        byte[] fileData, int maxThreads = 0, OutputFormat format = OutputFormat.Xml,
        bool validateChecksums = false, WevtCache? wevtCache = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default, bool salvage = false)
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

            // Same policy as Parse: a checksum failure skips the chunk outright rather than sending it to recovery
            if (validateChecksums)
            {
                ReadOnlySpan<byte> chunkData = span.Slice(offset, EvtxChunk.ChunkSize);
                EvtxChunkHeader header = EvtxChunkHeader.ParseEvtxChunkHeader(chunkData);
                if (!header.ValidateHeaderChecksum(chunkData) || !header.ValidateDataChecksum(chunkData))
                    continue;
            }

            validOffsets[validCount++] = offset;
        }

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
                Parallel.For(0, validCountCopy,
                    new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                    () => new Dictionary<Guid, CompiledTemplate?>(),
                    (i, state, localCache) =>
                    {
                        EvtxChunk chunk = EvtxChunk.Parse(fileData, validOffsets[i],
                            compiledCache: localCache, format, wevtCache, salvage);
                        channel.Writer.TryWrite((i, chunk));
                        return localCache;
                    },
                    _ => { });

                // Phase 4: parallel recovery of headerless chunks (indices offset by validCount)
                if (invalidCountCopy > 0)
                {
                    Parallel.For(0, invalidCountCopy,
                        new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                        () => new Dictionary<Guid, CompiledTemplate?>(),
                        (i, state, localCache) =>
                        {
                            EvtxChunk? recovered = EvtxChunk.ParseHeaderless(fileData, invalidOffsets[i],
                                compiledCache: localCache, format, wevtCache, salvage);
                            if (recovered != null)
                                channel.Writer.TryWrite((validCountCopy + i, recovered));
                            return localCache;
                        },
                        _ => { });
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
    /// Shared body of the <c>WriteTo</c> overloads. Pass 1 visits every chunk slot in file order: workers check the
    /// <c>ElfChnk</c> magic (and checksums when asked) on the chunk they load and render it if valid. Regions without
    /// the magic are only marked; pass 2 then recovers records from them, in file order, with fresh template caches.
    /// That is the same classification and order as <see cref="Parse"/>: checksum failures are skipped outright,
    /// magic-less regions go to recovery.
    /// </summary>
    /// <param name="source">Where chunk bytes come from.</param>
    /// <param name="chunkStart">File offset of the first chunk (the file header's block size, normally 4096).</param>
    /// <param name="length">Total file length in bytes; only whole 64 KB chunks are visited.</param>
    /// <param name="output">Stream that receives the UTF-8 output.</param>
    /// <param name="format">Output format (XML or JSON).</param>
    /// <param name="maxThreads">Thread count: 0/-1 = all cores, 1 = single-threaded, N = use N threads.</param>
    /// <param name="validateChecksums">When true, skip chunks that fail CRC32 header or data checksum validation.</param>
    /// <param name="wevtCache">Provider templates for records whose embedded template is missing or corrupt, or null.</param>
    /// <param name="salvage">True to keep the well-formed prefix of a record whose BinXml is corrupt instead of dropping it.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>Number of records written.</returns>
    private static int WriteAll(ChunkSource source, int chunkStart, long length, Stream output, OutputFormat format,
                                int maxThreads, bool validateChecksums, WevtCache? wevtCache, bool salvage,
                                CancellationToken cancellationToken)
    {
        int chunkCount = (int)Math.Max(0, (length - chunkStart) / EvtxChunk.ChunkSize);
        long[] offsets = new long[chunkCount];
        for (int i = 0; i < chunkCount; i++)
            offsets[i] = chunkStart + ((long)i * EvtxChunk.ChunkSize);

        int workers = maxThreads > 0 ? maxThreads : Environment.ProcessorCount;
        bool[] needsRecovery = new bool[chunkCount];
        int total = WriteChunks(source, offsets, chunkCount, ChunkPass.Classify, validateChecksums, needsRecovery,
            format, wevtCache, salvage, workers, output, cancellationToken);

        int recoverCount = 0;
        for (int i = 0; i < chunkCount; i++)
        {
            if (needsRecovery[i])
                offsets[recoverCount++] = offsets[i];
        }

        total += WriteChunks(source, offsets, recoverCount, ChunkPass.Recover, validateChecksums: false, null,
            format, wevtCache, salvage, workers, output, cancellationToken);
        return total;
    }

    /// <summary>
    /// Renders the given chunks and writes their UTF-8 output to <paramref name="output"/> in offset-array order.
    /// With several workers, chunks are rendered as a pipeline: workers claim chunks in order, load and render them
    /// into a ring of reusable buffers while the calling thread writes each chunk as soon as it is next in order, so
    /// loading, rendering and writing overlap and no worker waits on a batch barrier. At most
    /// <c>workers × chunks in flight per worker</c> rendered chunks are held at once. Each call starts with empty
    /// template caches, as each phase of <see cref="Parse"/> does, so recovered regions never see templates compiled
    /// for valid chunks.
    /// </summary>
    /// <param name="source">Where chunk bytes come from.</param>
    /// <param name="offsets">Absolute file offsets of the chunks to process.</param>
    /// <param name="count">Number of entries of <paramref name="offsets"/> to use.</param>
    /// <param name="pass">Classify-and-render valid chunks, or recover records from damaged regions.</param>
    /// <param name="validateChecksums">Classify pass only: skip chunks failing CRC32 validation.</param>
    /// <param name="needsRecovery">Classify pass only: set for each index whose region lacks the chunk magic.</param>
    /// <param name="format">Output format (XML or JSON).</param>
    /// <param name="wevtCache">Provider templates for records whose embedded template is missing or corrupt, or null.</param>
    /// <param name="salvage">True to keep the well-formed prefix of a record whose BinXml is corrupt instead of dropping it.</param>
    /// <param name="workers">Number of rendering threads (1 = render on the calling thread).</param>
    /// <param name="output">Stream that receives the UTF-8 output.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>Number of records written, counted as <see cref="EvtxChunk.RenderTo"/> counts them.</returns>
    private static int WriteChunks(ChunkSource source, long[] offsets, int count, ChunkPass pass,
                                   bool validateChecksums, bool[]? needsRecovery, OutputFormat format,
                                   WevtCache? wevtCache, bool salvage, int workers, Stream output,
                                   CancellationToken cancellationToken)
    {
        if (count == 0)
            return 0;

        int total = 0;
        if (workers == 1)
        {
            Dictionary<Guid, CompiledTemplate?> cache = new();
            ArrayBufferWriter<byte> sink = new(_initialSinkBytes);
            byte[]? chunkBuffer = source.NeedsBuffer ? new byte[EvtxChunk.ChunkSize] : null;
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sink.ResetWrittenCount();
                total += RenderChunk(source, offsets[i], i, pass, validateChecksums, needsRecovery, chunkBuffer,
                    cache, format, wevtCache, salvage, sink);
                output.Write(sink.WrittenSpan);
            }

            return total;
        }

        int depth = workers * _chunksInFlightPerWorker;
        ArrayBufferWriter<byte>?[] sinks = new ArrayBufferWriter<byte>?[depth];
        int[] kept = new int[depth];
        // ready[slot]: released by the worker that rendered the chunk in that slot, awaited by the writer.
        // free: one permit per ring slot not yet claimed; the writer returns a permit after writing a slot.
        // Claiming a permit before claiming the next index keeps every claimed index within `depth` of the next one
        // to write, so slot index % depth is always free when claimed.
        SemaphoreSlim[] ready = new SemaphoreSlim[depth];
        for (int k = 0; k < depth; k++)
            ready[k] = new SemaphoreSlim(0);
        using SemaphoreSlim free = new(depth);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? workerFailure = null;
        int next = -1;

        void Render()
        {
            // Template caches live for the worker's whole run, like the thread-local caches of Parse
            Dictionary<Guid, CompiledTemplate?> cache = new();
            byte[]? chunkBuffer = source.NeedsBuffer ? new byte[EvtxChunk.ChunkSize] : null;
            try
            {
                while (true)
                {
                    free.Wait(stop.Token);
                    int i = Interlocked.Increment(ref next);
                    if (i >= count)
                        return;

                    int slot = i % depth;
                    ArrayBufferWriter<byte> sink = sinks[slot] ??= new ArrayBufferWriter<byte>(_initialSinkBytes);
                    sink.ResetWrittenCount();
                    kept[slot] = RenderChunk(source, offsets[i], i, pass, validateChecksums, needsRecovery,
                        chunkBuffer, cache, format, wevtCache, salvage, sink);
                    ready[slot].Release();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Stopped by the writer (cancellation, a write failure, or another worker's failure)
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref workerFailure, ex, null);
                stop.Cancel();
            }
        }

        Task[] renderers = new Task[workers];
        for (int w = 0; w < workers; w++)
            renderers[w] = Task.Run(Render, CancellationToken.None);

        try
        {
            for (int i = 0; i < count; i++)
            {
                int slot = i % depth;
                ready[slot].Wait(stop.Token);
                output.Write(sinks[slot]!.WrittenSpan);
                total += kept[slot];
                free.Release();
            }
        }
        catch (OperationCanceledException) when (workerFailure != null)
        {
            // A worker failed and cancelled the pipeline; rethrown below like Parallel.For would
        }
        finally
        {
            stop.Cancel();
            Task.WaitAll(renderers);
            foreach (SemaphoreSlim slotReady in ready)
                slotReady.Dispose();
        }

        if (workerFailure != null)
            throw new AggregateException(workerFailure);

        return total;
    }

    /// <summary>
    /// Loads one chunk and renders it into <paramref name="sink"/>. In the classify pass, a region without the
    /// <c>ElfChnk\0</c> magic is marked in <paramref name="needsRecovery"/> and a chunk failing checksum validation is
    /// skipped; both produce no output.
    /// </summary>
    /// <param name="source">Where chunk bytes come from.</param>
    /// <param name="offset">Absolute file offset of the chunk.</param>
    /// <param name="index">Index of the chunk in this pass, used to mark <paramref name="needsRecovery"/>.</param>
    /// <param name="pass">Classify-and-render or recover.</param>
    /// <param name="validateChecksums">Classify pass only: skip chunks failing CRC32 validation.</param>
    /// <param name="needsRecovery">Classify pass only: per-index recovery flags.</param>
    /// <param name="chunkBuffer">The worker's 64 KB read buffer when the source is a file; otherwise null.</param>
    /// <param name="cache">The worker's cache of templates compiled for <paramref name="format"/>.</param>
    /// <param name="format">Output format (XML or JSON).</param>
    /// <param name="wevtCache">Provider templates for records whose embedded template is missing or corrupt, or null.</param>
    /// <param name="salvage">True to keep the well-formed prefix of a record whose BinXml is corrupt instead of dropping it.</param>
    /// <param name="sink">Buffer that receives the chunk's UTF-8 output.</param>
    /// <returns>Number of records rendered from the chunk.</returns>
    private static int RenderChunk(ChunkSource source, long offset, int index, ChunkPass pass, bool validateChecksums,
                                   bool[]? needsRecovery, byte[]? chunkBuffer,
                                   Dictionary<Guid, CompiledTemplate?> cache,
                                   OutputFormat format, WevtCache? wevtCache, bool salvage,
                                   ArrayBufferWriter<byte> sink)
    {
        int at = source.Load(offset, chunkBuffer, out byte[] data);
        if (pass == ChunkPass.Recover)
            return EvtxChunk.RenderTo(data, at, headerless: true, cache, format, sink, wevtCache, salvage);

        ReadOnlySpan<byte> chunkData = data.AsSpan(at, EvtxChunk.ChunkSize);
        if (!chunkData[..8].SequenceEqual("ElfChnk\0"u8))
        {
            needsRecovery![index] = true;
            return 0;
        }

        if (validateChecksums)
        {
            EvtxChunkHeader header = EvtxChunkHeader.ParseEvtxChunkHeader(chunkData);
            if (!header.ValidateHeaderChecksum(chunkData) || !header.ValidateDataChecksum(chunkData))
                return 0;
        }

        return EvtxChunk.RenderTo(data, at, headerless: false, cache, format, sink, wevtCache, salvage);
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Initial size of each per-worker UTF-8 output buffer used by <c>WriteTo</c> (128 KB).
    /// A 64 KB chunk typically renders to 100-200 KB of text; buffers grow on demand and are reused.
    /// </summary>
    private const int _initialSinkBytes = 128 * 1024;

    /// <summary>
    /// Rendered-but-unwritten chunks allowed per worker in the <c>WriteTo</c> pipeline. More lets fast workers
    /// run further ahead of a slow chunk; fewer bounds memory to workers × this × chunk output size (~100-200 KB).
    /// </summary>
    private const int _chunksInFlightPerWorker = 4;

    /// <summary>
    /// Bytes read from the start of a file to parse its header: the 4 KB header block (only the first 128 bytes are
    /// decoded).
    /// </summary>
    private const int _fileHeaderReadBytes = 4096;

    #endregion

    #region Nested Types

    /// <summary>
    /// Which of <see cref="WriteAll"/>'s two passes a <see cref="WriteChunks"/> call runs.
    /// </summary>
    private enum ChunkPass
    {
        /// <summary>
        /// Check each region's chunk magic (and checksums when asked), render valid chunks, mark the rest for recovery.
        /// </summary>
        Classify,

        /// <summary>
        /// Recover records from regions whose chunk header is damaged or missing.
        /// </summary>
        Recover,
    }

    /// <summary>
    /// Where <c>WriteTo</c> gets chunk bytes: a slice of an in-memory file, or a positional read from a file handle
    /// into the worker's own 64 KB buffer. Rendering only touches the chunk's own bytes (see
    /// <see cref="BinXml.BinXmlParser"/>), so both give identical output.
    /// </summary>
    /// <param name="fileData">Whole file in memory, or null when reading from <paramref name="file"/>.</param>
    /// <param name="file">Open file handle, or null when <paramref name="fileData"/> is set.</param>
    private readonly struct ChunkSource(byte[]? fileData, SafeFileHandle? file)
    {
        /// <summary>
        /// True when each worker needs its own 64 KB buffer to read chunks into.
        /// </summary>
        public bool NeedsBuffer => fileData == null;

        /// <summary>
        /// Makes the 64 KB chunk at <paramref name="offset"/> available.
        /// </summary>
        /// <param name="offset">Absolute file offset of the chunk.</param>
        /// <param name="buffer">The worker's read buffer (file sources only).</param>
        /// <param name="data">Array holding the chunk.</param>
        /// <returns>Offset of the chunk within <paramref name="data"/>.</returns>
        public int Load(long offset, byte[]? buffer, out byte[] data)
        {
            if (fileData != null)
            {
                data = fileData;
                return (int)offset;
            }

            ReadFully(file!, buffer, offset);
            data = buffer!;
            return 0;
        }

        /// <summary>
        /// Fills <paramref name="destination"/> from <paramref name="file"/> at <paramref name="offset"/>, looping over
        /// short reads.
        /// </summary>
        /// <param name="file">Open file handle.</param>
        /// <param name="destination">Bytes to fill.</param>
        /// <param name="offset">File offset to read from.</param>
        /// <returns>The number of bytes read (always <c>destination.Length</c>).</returns>
        /// <exception cref="EndOfStreamException">The file ended first (it shrank while being read).</exception>
        public static int ReadFully(SafeFileHandle file, Span<byte> destination, long offset)
        {
            int filled = 0;
            while (filled < destination.Length)
            {
                int read = RandomAccess.Read(file, destination[filled..], offset + filled);
                if (read == 0)
                    throw new EndOfStreamException($"EVTX file ended at {offset + filled} while reading {destination.Length} bytes at {offset}");
                filled += read;
            }

            return filled;
        }
    }

    #endregion
}