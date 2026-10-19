using System.Buffers;
using System.Runtime.CompilerServices;
using AxoParse.Evtx.BinXml;
using Microsoft.Win32.SafeHandles;

namespace AxoParse.Evtx;

/// <summary>
/// Entry points for reading EVTX files. Every operation takes an optional <see cref="EvtxOptions"/> (output format,
/// threads, checksum validation, salvage mode, provider templates) and is thread-safe: calls share no state, and
/// options and results are immutable.
/// <para>
/// Chunks are parsed in parallel and results come back in a fixed order whatever the thread count: valid chunks in
/// file order, then records recovered from regions whose chunk header is damaged.
/// </para>
/// <list type="bullet">
/// <item><see cref="Parse(byte[], EvtxOptions?, CancellationToken)"/> returns the whole log.</item>
/// <item><see cref="ReadEvents(string, EvtxOptions?, CancellationToken)"/> and
/// <see cref="ReadEventsAsync(string, EvtxOptions?, CancellationToken)"/> stream events with bounded memory.</item>
/// <item><see cref="WriteTo(string, Stream, EvtxOptions?, CancellationToken)"/> writes every event's output to a
/// stream, the fastest way to convert a file.</item>
/// <item><see cref="ParseChunk"/> and <see cref="ParseFileHeader"/> decode single chunks, e.g. for carving or for
/// reading a file chunk by chunk.</item>
/// </list>
/// </summary>
public static class EvtxParser
{
    #region Public Methods

    /// <summary>
    /// Parses an entire EVTX file held in memory.
    /// </summary>
    /// <param name="fileData">Complete EVTX file bytes.</param>
    /// <param name="options">Parse options, or null for <see cref="EvtxOptions.Default"/>.</param>
    /// <param name="cancellationToken">Token to cancel the parse.</param>
    /// <returns>The parsed log.</returns>
    /// <exception cref="EvtxParseException">The file header is missing or invalid.</exception>
    public static EvtxLog Parse(byte[] fileData, EvtxOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileData);
        return ParseLog(ChunkSource.FromArray(fileData), options ?? EvtxOptions.Default, cancellationToken);
    }

    /// <summary>
    /// Parses an entire EVTX file from disk. Chunks are read as they are parsed, so only the parsed output is held in
    /// memory, not the file, and files over 2 GB work. The file is opened with <see cref="FileShare.ReadWrite"/>, so a
    /// log another process still has open can be read.
    /// </summary>
    /// <param name="path">Path of the .evtx file.</param>
    /// <param name="options">Parse options, or null for <see cref="EvtxOptions.Default"/>.</param>
    /// <param name="cancellationToken">Token to cancel the parse.</param>
    /// <returns>The parsed log.</returns>
    /// <exception cref="EvtxParseException">The file header is missing or invalid.</exception>
    public static EvtxLog Parse(string path, EvtxOptions? options = null, CancellationToken cancellationToken = default)
    {
        using SafeFileHandle file = OpenRead(path);
        return ParseLog(ChunkSource.FromHandle(file), options ?? EvtxOptions.Default, cancellationToken);
    }

    /// <summary>
    /// Decodes one standalone 64 KB chunk, e.g. carved from disk or read from a file chunk by chunk. Offsets are
    /// relative to the chunk: <see cref="EvtxChunk.FileOffset"/> is 0. Templates compiled for one call are not
    /// reused by the next.
    /// </summary>
    /// <param name="chunkData">The chunk: at least 64 KB, of which the first 64 KB are used.</param>
    /// <param name="options">Output format, salvage mode and provider templates, or null for the defaults
    /// (<see cref="EvtxOptions.MaxThreads"/> and <see cref="EvtxOptions.ValidateChecksums"/> are ignored).</param>
    /// <returns>The parsed chunk.</returns>
    /// <exception cref="ArgumentException"><paramref name="chunkData"/> is shorter than 64 KB.</exception>
    /// <exception cref="EvtxParseException">The chunk signature (<c>ElfChnk\0</c>) is missing.</exception>
    public static EvtxChunk ParseChunk(ReadOnlySpan<byte> chunkData, EvtxOptions? options = null)
    {
        if (chunkData.Length < EvtxChunk.ChunkSize)
            throw new ArgumentException($"A chunk is {EvtxChunk.ChunkSize} bytes; got {chunkData.Length}.", nameof(chunkData));

        byte[] chunk = chunkData[..EvtxChunk.ChunkSize].ToArray();
        return EvtxChunk.Parse(chunk, 0, 0, new Dictionary<Guid, CompiledTemplate?>(),
            options ?? EvtxOptions.Default);
    }

    /// <summary>
    /// Parses the EVTX file header: the first 128 bytes of the file, starting with the <c>ElfFile\0</c> signature.
    /// For callers that read a file chunk by chunk (see <see cref="ParseChunk"/>).
    /// </summary>
    /// <param name="data">At least the first 128 bytes of the file.</param>
    /// <returns>The parsed header.</returns>
    /// <exception cref="EvtxParseException">The data is shorter than 128 bytes or the signature is wrong.</exception>
    public static EvtxFileHeader ParseFileHeader(ReadOnlySpan<byte> data) => EvtxFileHeader.ParseEvtxFileHeader(data);

    /// <summary>
    /// Streams the events of an EVTX file held in memory, in output order, while later chunks are parsed in the
    /// background. Only a few chunks per worker are held at once. Stopping the enumeration early stops the workers.
    /// </summary>
    /// <param name="fileData">Complete EVTX file bytes.</param>
    /// <param name="options">Parse options, or null for <see cref="EvtxOptions.Default"/>.</param>
    /// <param name="cancellationToken">Token to cancel the enumeration.</param>
    /// <returns>The events.</returns>
    /// <exception cref="EvtxParseException">The file header is missing or invalid.</exception>
    public static IEnumerable<EvtxEvent> ReadEvents(byte[] fileData, EvtxOptions? options = null,
                                                    CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileData);
        ChunkSource source = ChunkSource.FromArray(fileData);
        EvtxFileHeader header = source.ReadHeader();
        return Flatten(ReadChunks(source, header, options ?? EvtxOptions.Default, null, null, cancellationToken));
    }

    /// <summary>
    /// Streams the events of an EVTX file from disk, in output order, while later chunks are read and parsed in the
    /// background. Memory stays at a few chunks per worker whatever the file size. The file is opened (with
    /// <see cref="FileShare.ReadWrite"/>) when enumeration starts and closed when it ends.
    /// </summary>
    /// <param name="path">Path of the .evtx file.</param>
    /// <param name="options">Parse options, or null for <see cref="EvtxOptions.Default"/>.</param>
    /// <param name="cancellationToken">Token to cancel the enumeration.</param>
    /// <returns>The events.</returns>
    /// <exception cref="EvtxParseException">The file header is missing or invalid.</exception>
    public static IEnumerable<EvtxEvent> ReadEvents(string path, EvtxOptions? options = null,
                                                    CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return ReadFileEvents(path, options ?? EvtxOptions.Default, cancellationToken);
    }

    /// <summary>
    /// Asynchronous form of <see cref="ReadEvents(byte[], EvtxOptions?, CancellationToken)"/>: waits for the next
    /// chunk without blocking a thread.
    /// </summary>
    /// <param name="fileData">Complete EVTX file bytes.</param>
    /// <param name="options">Parse options, or null for <see cref="EvtxOptions.Default"/>.</param>
    /// <param name="cancellationToken">Token to cancel the enumeration.</param>
    /// <returns>The events.</returns>
    /// <exception cref="EvtxParseException">The file header is missing or invalid.</exception>
    public static IAsyncEnumerable<EvtxEvent> ReadEventsAsync(byte[] fileData, EvtxOptions? options = null,
                                                              CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileData);
        ChunkSource source = ChunkSource.FromArray(fileData);
        EvtxFileHeader header = source.ReadHeader();
        return ReadEventsAsync(source, header, null, options ?? EvtxOptions.Default, cancellationToken);
    }

    /// <summary>
    /// Asynchronous form of <see cref="ReadEvents(string, EvtxOptions?, CancellationToken)"/>: waits for the next
    /// chunk without blocking a thread.
    /// </summary>
    /// <param name="path">Path of the .evtx file.</param>
    /// <param name="options">Parse options, or null for <see cref="EvtxOptions.Default"/>.</param>
    /// <param name="cancellationToken">Token to cancel the enumeration.</param>
    /// <returns>The events.</returns>
    /// <exception cref="EvtxParseException">The file header is missing or invalid.</exception>
    public static IAsyncEnumerable<EvtxEvent> ReadEventsAsync(string path, EvtxOptions? options = null,
                                                              CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return ReadEventsAsync(null, default, path, options ?? EvtxOptions.Default, cancellationToken);
    }

    /// <summary>
    /// Parses an EVTX file held in memory and writes every event's output to <paramref name="output"/> as UTF-8, in
    /// the same order and with the same bytes as the events of
    /// <see cref="Parse(byte[], EvtxOptions?, CancellationToken)"/>. Chunks are rendered into reusable per-worker
    /// buffers and written as soon as they are next in order, so memory stays flat and no event is retained.
    /// Diagnostics are not reported; a record that fails to render writes nothing.
    /// </summary>
    /// <param name="fileData">Complete EVTX file bytes.</param>
    /// <param name="output">Stream that receives the output. It is written to but not flushed or closed.</param>
    /// <param name="options">Parse options, or null for <see cref="EvtxOptions.Default"/>.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>Number of records written, counted as <see cref="EvtxLog.RecordCount"/> counts them.</returns>
    /// <exception cref="EvtxParseException">The file header is missing or invalid.</exception>
    public static int WriteTo(byte[] fileData, Stream output, EvtxOptions? options = null,
                              CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileData);
        return WriteAll(ChunkSource.FromArray(fileData), output, options ?? EvtxOptions.Default, cancellationToken);
    }

    /// <summary>
    /// Streams an EVTX file from disk to <paramref name="output"/>; see
    /// <see cref="WriteTo(byte[], Stream, EvtxOptions?, CancellationToken)"/>. Each worker reads the 64 KB chunk it
    /// is about to render, so reading overlaps rendering and memory stays at a few buffers per worker whatever the
    /// file size (files over 2 GB work). The file is opened with <see cref="FileShare.ReadWrite"/>, so a log another
    /// process still has open can be read.
    /// </summary>
    /// <param name="path">Path of the .evtx file.</param>
    /// <param name="output">Stream that receives the output. It is written to but not flushed or closed.</param>
    /// <param name="options">Parse options, or null for <see cref="EvtxOptions.Default"/>.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>Number of records written.</returns>
    /// <exception cref="EvtxParseException">The file header is missing or invalid.</exception>
    public static int WriteTo(string path, Stream output, EvtxOptions? options = null,
                              CancellationToken cancellationToken = default)
    {
        using SafeFileHandle file = OpenRead(path);
        return WriteAll(ChunkSource.FromHandle(file), output, options ?? EvtxOptions.Default, cancellationToken);
    }

    /// <summary>
    /// Streams an EVTX file from a seekable stream to <paramref name="output"/>; see
    /// <see cref="WriteTo(string, Stream, EvtxOptions?, CancellationToken)"/>. The input's content from position 0 is
    /// the file. A <see cref="FileStream"/> is read with positional reads through its handle (its position is left
    /// alone); any other stream is read one chunk at a time and its position changes.
    /// </summary>
    /// <param name="input">Readable, seekable stream holding the file. It is not closed.</param>
    /// <param name="output">Stream that receives the output. It is written to but not flushed or closed.</param>
    /// <param name="options">Parse options, or null for <see cref="EvtxOptions.Default"/>.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>Number of records written.</returns>
    /// <exception cref="ArgumentException"><paramref name="input"/> can't be read or can't seek.</exception>
    /// <exception cref="EvtxParseException">The file header is missing or invalid.</exception>
    public static int WriteTo(Stream input, Stream output, EvtxOptions? options = null,
                              CancellationToken cancellationToken = default) =>
        WriteAll(ChunkSource.FromStream(input), output, options ?? EvtxOptions.Default, cancellationToken);

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// File offsets of every whole 64 KB chunk slot after the header block.
    /// </summary>
    /// <param name="header">Parsed file header.</param>
    /// <param name="length">Total file length in bytes; a trailing partial chunk is ignored.</param>
    /// <returns>The chunk offsets in file order.</returns>
    private static long[] ChunkOffsets(EvtxFileHeader header, long length)
    {
        int chunkStart = header.HeaderBlockSize;
        int chunkCount = (int)Math.Max(0, (length - chunkStart) / EvtxChunk.ChunkSize);
        long[] offsets = new long[chunkCount];
        for (int i = 0; i < chunkCount; i++)
            offsets[i] = chunkStart + ((long)i * EvtxChunk.ChunkSize);
        return offsets;
    }

    /// <summary>
    /// Pass 1 of every operation: decides what a chunk slot is. Regions without the <c>ElfChnk\0</c> magic go to the
    /// recovery pass; with <see cref="EvtxOptions.ValidateChecksums"/>, chunks failing CRC32 validation are skipped.
    /// </summary>
    /// <param name="chunkData">The 64 KB chunk slot.</param>
    /// <param name="options">Parse options.</param>
    /// <returns>What to do with the slot.</returns>
    private static ChunkStatus Classify(ReadOnlySpan<byte> chunkData, EvtxOptions options)
    {
        if (!chunkData[..8].SequenceEqual("ElfChnk\0"u8))
            return ChunkStatus.NeedsRecovery;

        if (options.ValidateChecksums)
        {
            EvtxChunkHeader header = EvtxChunkHeader.ParseEvtxChunkHeader(chunkData);
            if (!header.ValidateHeaderChecksum(chunkData) || !header.ValidateDataChecksum(chunkData))
                return ChunkStatus.ChecksumMismatch;
        }

        return ChunkStatus.Valid;
    }

    /// <summary>
    /// Parses one chunk slot into <paramref name="slot"/> for the event-producing operations.
    /// </summary>
    /// <param name="source">Where chunk bytes come from.</param>
    /// <param name="offset">Absolute file offset of the slot.</param>
    /// <param name="recover">True for the recovery pass (headerless parse), false for the classify pass.</param>
    /// <param name="options">Parse options.</param>
    /// <param name="worker">The worker's template cache and read buffer.</param>
    /// <param name="slot">Result holder to fill (a new one when null).</param>
    /// <returns>The filled result holder.</returns>
    private static ChunkSlot ParseSlot(ChunkSource source, long offset, bool recover, EvtxOptions options,
                                       ChunkWorker worker, ChunkSlot? slot)
    {
        slot ??= new ChunkSlot();
        int at = source.Load(offset, worker.Buffer, out byte[] data);
        if (recover)
        {
            slot.Status = ChunkStatus.Valid;
            slot.Chunk = EvtxChunk.ParseHeaderless(data, at, offset, worker.Cache, options);
            return slot;
        }

        slot.Status = Classify(data.AsSpan(at, EvtxChunk.ChunkSize), options);
        slot.Chunk = slot.Status == ChunkStatus.Valid ? EvtxChunk.Parse(data, at, offset, worker.Cache, options) : null;
        return slot;
    }

    /// <summary>
    /// Streams the parsed chunks of a file in output order: pass 1 parses valid chunks in file order and marks
    /// regions without the chunk magic; pass 2 recovers records from those regions, in file order, with fresh template
    /// caches (so recovered regions never see templates compiled for valid chunks).
    /// </summary>
    /// <param name="source">Where chunk bytes come from.</param>
    /// <param name="header">Parsed file header.</param>
    /// <param name="options">Parse options.</param>
    /// <param name="diagnostics">Receives a diagnostic per chunk skipped for a checksum mismatch, or null.</param>
    /// <param name="file">Handle to dispose when the enumeration ends, or null.</param>
    /// <param name="cancellationToken">Token to cancel the enumeration.</param>
    /// <returns>The parsed chunks.</returns>
    private static IEnumerable<EvtxChunk> ReadChunks(ChunkSource source, EvtxFileHeader header, EvtxOptions options,
                                                     List<EvtxDiagnostic>? diagnostics, SafeFileHandle? file,
                                                     CancellationToken cancellationToken)
    {
        using SafeFileHandle? ownedFile = file;
        long[] offsets = ChunkOffsets(header, source.Length);
        int recoverCount = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            bool recover = pass == 1;
            int count = recover ? recoverCount : offsets.Length;
            using ChunkPipeline<ChunkSlot> pipeline = new(count, options.Workers, source.NeedsBuffer,
                (i, worker, slot) => ParseSlot(source, offsets[i], recover, options, worker, slot), cancellationToken);
            for (int i = 0; i < count; i++)
            {
                ChunkSlot slot = pipeline.Take(i);
                EvtxChunk? chunk = slot.Chunk;
                if (slot.Status == ChunkStatus.NeedsRecovery)
                    offsets[recoverCount++] = offsets[i];
                else if (slot.Status == ChunkStatus.ChecksumMismatch)
                    diagnostics?.Add(ChecksumDiagnostic(offsets[i]));
                pipeline.Release();

                if (chunk != null)
                    yield return chunk;
            }
        }
    }

    /// <summary>
    /// Asynchronous form of <see cref="ReadChunks"/>, flattened to events. Opens <paramref name="path"/> when
    /// enumeration starts if <paramref name="source"/> is null.
    /// </summary>
    /// <param name="source">Where chunk bytes come from, or null to open <paramref name="path"/>.</param>
    /// <param name="header">Parsed file header (when <paramref name="source"/> is given).</param>
    /// <param name="path">File to open when <paramref name="source"/> is null.</param>
    /// <param name="options">Parse options.</param>
    /// <param name="cancellationToken">Token to cancel the enumeration.</param>
    /// <returns>The events.</returns>
    private static async IAsyncEnumerable<EvtxEvent> ReadEventsAsync(ChunkSource? source, EvtxFileHeader header,
                                                                     string? path, EvtxOptions options,
                                                                     [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using SafeFileHandle? file = source == null ? OpenRead(path!) : null;
        if (source == null)
        {
            source = ChunkSource.FromHandle(file!);
            header = source.ReadHeader();
        }

        long[] offsets = ChunkOffsets(header, source.Length);
        int recoverCount = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            bool recover = pass == 1;
            int count = recover ? recoverCount : offsets.Length;
            ChunkSource chunkSource = source;
            using ChunkPipeline<ChunkSlot> pipeline = new(count, options.Workers, source.NeedsBuffer,
                (i, worker, slot) => ParseSlot(chunkSource, offsets[i], recover, options, worker, slot),
                cancellationToken);
            for (int i = 0; i < count; i++)
            {
                ChunkSlot slot = await pipeline.TakeAsync(i).ConfigureAwait(false);
                EvtxChunk? chunk = slot.Chunk;
                if (slot.Status == ChunkStatus.NeedsRecovery)
                    offsets[recoverCount++] = offsets[i];
                pipeline.Release();

                if (chunk == null)
                    continue;
                for (int e = 0; e < chunk.Count; e++)
                    yield return new EvtxEvent(chunk, e);
            }
        }
    }

    /// <summary>
    /// Diagnostic for a chunk skipped because its checksums don't match.
    /// </summary>
    /// <param name="offset">Absolute file offset of the chunk.</param>
    /// <returns>The diagnostic.</returns>
    private static EvtxDiagnostic ChecksumDiagnostic(long offset) =>
        new(EvtxDiagnosticKind.ChunkChecksumMismatch, null, offset,
            $"Chunk at offset 0x{offset:X} skipped: CRC32 checksum validation failed");

    /// <summary>
    /// Flattens chunks to their events.
    /// </summary>
    /// <param name="chunks">Chunks in output order.</param>
    /// <returns>The events.</returns>
    private static IEnumerable<EvtxEvent> Flatten(IEnumerable<EvtxChunk> chunks)
    {
        foreach (EvtxChunk chunk in chunks)
        {
            for (int i = 0; i < chunk.Count; i++)
                yield return new EvtxEvent(chunk, i);
        }
    }

    /// <summary>
    /// Opens a file for reading, allowing other processes to keep writing it.
    /// </summary>
    /// <param name="path">Path of the file.</param>
    /// <returns>The open handle.</returns>
    private static SafeFileHandle OpenRead(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    /// <summary>
    /// Shared body of the <c>Parse</c> overloads: collects every chunk and diagnostic in output order.
    /// </summary>
    /// <param name="source">Where chunk bytes come from.</param>
    /// <param name="options">Parse options.</param>
    /// <param name="cancellationToken">Token to cancel the parse.</param>
    /// <returns>The parsed log.</returns>
    private static EvtxLog ParseLog(ChunkSource source, EvtxOptions options, CancellationToken cancellationToken)
    {
        EvtxFileHeader header = source.ReadHeader();
        List<EvtxDiagnostic> diagnostics = new();
        List<EvtxChunk> chunks = new();
        int recordCount = 0;
        foreach (EvtxChunk chunk in ReadChunks(source, header, options, diagnostics, null, cancellationToken))
        {
            chunks.Add(chunk);
            recordCount += chunk.Count;
            chunk.CollectDiagnostics(diagnostics);
        }

        return new EvtxLog(header, chunks.ToArray(), recordCount, diagnostics.ToArray());
    }

    /// <summary>
    /// Opens <paramref name="path"/> and streams its events; the file is opened on the first
    /// <see cref="System.Collections.IEnumerator.MoveNext"/> and closed when the enumeration ends.
    /// </summary>
    /// <param name="path">Path of the .evtx file.</param>
    /// <param name="options">Parse options.</param>
    /// <param name="cancellationToken">Token to cancel the enumeration.</param>
    /// <returns>The events.</returns>
    private static IEnumerable<EvtxEvent> ReadFileEvents(string path, EvtxOptions options,
                                                         CancellationToken cancellationToken)
    {
        SafeFileHandle file = OpenRead(path);
        ChunkSource source;
        EvtxFileHeader header;
        try
        {
            source = ChunkSource.FromHandle(file);
            header = source.ReadHeader();
        }
        catch
        {
            file.Dispose();
            throw;
        }

        // ReadChunks disposes the handle when the enumeration ends
        foreach (EvtxEvent evt in Flatten(ReadChunks(source, header, options, null, file, cancellationToken)))
            yield return evt;
    }

    /// <summary>
    /// Shared body of the <c>WriteTo</c> overloads: the same two passes as <see cref="ReadChunks"/>, rendering each
    /// chunk into a reusable buffer that is written as soon as the chunk is next in order.
    /// </summary>
    /// <param name="source">Where chunk bytes come from.</param>
    /// <param name="output">Stream that receives the output.</param>
    /// <param name="options">Parse options.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>Number of records written.</returns>
    private static int WriteAll(ChunkSource source, Stream output, EvtxOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        EvtxFileHeader header = source.ReadHeader();
        long[] offsets = ChunkOffsets(header, source.Length);
        int total = 0;
        int recoverCount = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            bool recover = pass == 1;
            int count = recover ? recoverCount : offsets.Length;
            using ChunkPipeline<RenderSlot> pipeline = new(count, options.Workers, source.NeedsBuffer,
                (i, worker, slot) => RenderChunkSlot(source, offsets[i], recover, options, worker, slot), cancellationToken);
            for (int i = 0; i < count; i++)
            {
                RenderSlot slot = pipeline.Take(i);
                if (slot.Status == ChunkStatus.NeedsRecovery)
                    offsets[recoverCount++] = offsets[i];
                output.Write(slot.Sink.WrittenSpan);
                total += slot.Kept;
                pipeline.Release();
            }
        }

        return total;
    }

    /// <summary>
    /// Renders one chunk slot into <paramref name="slot"/>'s reusable buffer for <c>WriteTo</c>.
    /// </summary>
    /// <param name="source">Where chunk bytes come from.</param>
    /// <param name="offset">Absolute file offset of the slot.</param>
    /// <param name="recover">True for the recovery pass (headerless render), false for the classify pass.</param>
    /// <param name="options">Parse options.</param>
    /// <param name="worker">The worker's template cache and read buffer.</param>
    /// <param name="slot">Result holder to reuse (a new one when null).</param>
    /// <returns>The filled result holder.</returns>
    private static RenderSlot RenderChunkSlot(ChunkSource source, long offset, bool recover, EvtxOptions options,
                                         ChunkWorker worker, RenderSlot? slot)
    {
        slot ??= new RenderSlot();
        slot.Sink.ResetWrittenCount();
        slot.Kept = 0;
        int at = source.Load(offset, worker.Buffer, out byte[] data);
        slot.Status = recover ? ChunkStatus.Valid : Classify(data.AsSpan(at, EvtxChunk.ChunkSize), options);
        if (slot.Status == ChunkStatus.Valid)
            slot.Kept = EvtxChunk.RenderTo(data, at, recover, worker.Cache, options, slot.Sink);
        return slot;
    }

    #endregion

    #region Nested Types

    /// <summary>
    /// What pass 1 decided about a chunk slot.
    /// </summary>
    private enum ChunkStatus
    {
        /// <summary>
        /// The chunk has its magic (and valid checksums when checked) and is parsed.
        /// </summary>
        Valid,

        /// <summary>
        /// The region lacks the chunk magic; its records are recovered in pass 2.
        /// </summary>
        NeedsRecovery,

        /// <summary>
        /// The chunk failed checksum validation and is skipped.
        /// </summary>
        ChecksumMismatch,
    }

    /// <summary>
    /// Result of parsing one chunk slot.
    /// </summary>
    private sealed class ChunkSlot
    {
        /// <summary>
        /// The parsed chunk, or null if the slot was skipped or recovered nothing.
        /// </summary>
        public EvtxChunk? Chunk;

        /// <summary>
        /// What pass 1 decided about the slot.
        /// </summary>
        public ChunkStatus Status;
    }

    /// <summary>
    /// Result of rendering one chunk slot for <c>WriteTo</c>; reused across chunks.
    /// </summary>
    private sealed class RenderSlot
    {
        /// <summary>
        /// Records rendered from the slot.
        /// </summary>
        public int Kept;

        /// <summary>
        /// The slot's UTF-8 output (128 KB to start: a 64 KB chunk typically renders to 100-200 KB; grows on demand).
        /// </summary>
        public readonly ArrayBufferWriter<byte> Sink = new(128 * 1024);

        /// <summary>
        /// What pass 1 decided about the slot.
        /// </summary>
        public ChunkStatus Status;
    }

    #endregion
}
