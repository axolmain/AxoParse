using AxoParse.Evtx.BinXml;

namespace AxoParse.Evtx;

/// <summary>
/// Per-worker state for chunk work: the cache of templates compiled for the output format (kept for the worker's
/// whole run, so a template is compiled once per worker) and, for file and stream sources, the 64 KB read buffer.
/// </summary>
/// <param name="needsBuffer">True to give the worker a read buffer.</param>
internal sealed class ChunkWorker(bool needsBuffer)
{
    /// <summary>
    /// Templates compiled for the output format, keyed by GUID.
    /// </summary>
    public readonly Dictionary<Guid, CompiledTemplate?> Cache = new();

    /// <summary>
    /// 64 KB buffer chunks are read into, or null when the source is in memory.
    /// </summary>
    public readonly byte[]? Buffer = needsBuffer ? new byte[EvtxChunk.ChunkSize] : null;
}

/// <summary>
/// Runs one piece of work per chunk index on worker threads and hands the results to a single consumer in index order.
/// Workers claim indices in order and write into a ring of result slots while the consumer takes each index as soon as
/// it is next, so loading, rendering and consuming overlap and no worker waits on a batch barrier. At most
/// <c>workers × chunks in flight per worker</c> results are held at once, so memory stays flat whatever the file size.
/// With one worker the work runs inline on the consumer's thread when each index is taken.
/// <para>
/// The consumer calls <see cref="Take"/> (or <see cref="TakeAsync"/>) for indices 0, 1, 2, … in order and
/// <see cref="Release"/> after each, once it no longer needs the result (the slot is then reused). Disposing stops the
/// workers and waits for them. A worker failure is rethrown from the next take as an <see cref="AggregateException"/>.
/// </para>
/// </summary>
/// <typeparam name="TResult">Result of one chunk's work.</typeparam>
internal sealed class ChunkPipeline<TResult> : IDisposable
    where TResult : class
{
    #region Constructors And Destructors

    /// <summary>
    /// Starts the workers.
    /// </summary>
    /// <param name="count">Number of chunk indices to process.</param>
    /// <param name="workers">Number of worker threads (1 = run work inline on the consumer's thread).</param>
    /// <param name="needsBuffer">True to give each worker a 64 KB read buffer.</param>
    /// <param name="work">The work for one index. It receives the worker's state and the result previously held by
    /// the index's slot (null on first use), which it may reuse, and returns the index's result.</param>
    /// <param name="cancellationToken">Token that stops the work.</param>
    public ChunkPipeline(int count, int workers, bool needsBuffer, Func<int, ChunkWorker, TResult?, TResult> work,
                         CancellationToken cancellationToken)
    {
        _count = count;
        _work = work;
        _cancellationToken = cancellationToken;
        if ((workers <= 1) || (count <= 1))
        {
            _inlineWorker = new ChunkWorker(needsBuffer);
            return;
        }

        int depth = workers * _chunksInFlightPerWorker;
        _results = new TResult?[depth];
        // ready[slot]: released by the worker that filled the slot, awaited by the consumer.
        // free: one permit per ring slot not yet claimed; the consumer returns a permit after each release.
        // Claiming a permit before claiming the next index keeps every claimed index within `depth` of the next one
        // to take, so slot index % depth is always free when claimed.
        _ready = new SemaphoreSlim[depth];
        for (int k = 0; k < depth; k++)
            _ready[k] = new SemaphoreSlim(0);
        _free = new SemaphoreSlim(depth);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _workers = new Task[Math.Min(workers, count)];
        for (int w = 0; w < _workers.Length; w++)
            _workers[w] = Task.Run(() => RunWorker(needsBuffer), CancellationToken.None);
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Stops the workers (if the consumer stopped early) and waits for them to exit.
    /// </summary>
    public void Dispose()
    {
        if (_workers == null)
            return;

        _stop!.Cancel();
        Task.WaitAll(_workers);
        foreach (SemaphoreSlim ready in _ready!)
            ready.Dispose();
        _free!.Dispose();
        _stop.Dispose();
        _workers = null;
    }

    /// <summary>
    /// Releases the slot of the index last taken so a worker can reuse it.
    /// </summary>
    public void Release() => _free?.Release();

    /// <summary>
    /// Returns the result for <paramref name="index"/>, waiting until a worker has produced it.
    /// </summary>
    /// <param name="index">The next index; indices must be taken in order.</param>
    /// <returns>The index's result.</returns>
    public TResult Take(int index)
    {
        if (_inlineWorker != null)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            return _inlineResult = _work(index, _inlineWorker, _inlineResult);
        }

        int slot = index % _results!.Length;
        try
        {
            _ready![slot].Wait(_stop!.Token);
        }
        catch (OperationCanceledException) when (_workerFailure != null)
        {
            throw new AggregateException(_workerFailure);
        }

        return _results[slot]!;
    }

    /// <summary>
    /// Returns the result for <paramref name="index"/>, waiting asynchronously until a worker has produced it.
    /// </summary>
    /// <param name="index">The next index; indices must be taken in order.</param>
    /// <returns>The index's result.</returns>
    public async ValueTask<TResult> TakeAsync(int index)
    {
        if (_inlineWorker != null)
            return Take(index);

        int slot = index % _results!.Length;
        try
        {
            await _ready![slot].WaitAsync(_stop!.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_workerFailure != null)
        {
            throw new AggregateException(_workerFailure);
        }

        return _results[slot]!;
    }

    #endregion

    #region Non-Public Methods

    /// <summary>
    /// Worker loop: claim the next index once a slot is free, do its work into the slot, signal the consumer.
    /// </summary>
    /// <param name="needsBuffer">True to give the worker a 64 KB read buffer.</param>
    private void RunWorker(bool needsBuffer)
    {
        ChunkWorker worker = new(needsBuffer);
        try
        {
            while (true)
            {
                _free!.Wait(_stop!.Token);
                int i = Interlocked.Increment(ref _next);
                if (i >= _count)
                    return;

                int slot = i % _results!.Length;
                _results[slot] = _work(i, worker, _results[slot]);
                _ready![slot].Release();
            }
        }
        catch (OperationCanceledException) when (_stop!.IsCancellationRequested)
        {
            // Stopped by the consumer (cancellation, early exit, or another worker's failure)
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _workerFailure, ex, null);
            _stop!.Cancel();
        }
    }

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Results allowed in flight per worker. More lets fast workers run further ahead of a slow chunk; fewer bounds
    /// memory to workers × this × one chunk's result (~100-200 KB of output).
    /// </summary>
    private const int _chunksInFlightPerWorker = 4;

    /// <summary>
    /// The caller's cancellation token.
    /// </summary>
    private readonly CancellationToken _cancellationToken;

    /// <summary>
    /// Number of indices to process.
    /// </summary>
    private readonly int _count;

    /// <summary>
    /// Permits for unclaimed ring slots (multi-worker only).
    /// </summary>
    private readonly SemaphoreSlim? _free;

    /// <summary>
    /// Result of the last index run inline (single worker only), offered back to the work for reuse.
    /// </summary>
    private TResult? _inlineResult;

    /// <summary>
    /// State of the inline worker, or null when workers run on their own threads.
    /// </summary>
    private readonly ChunkWorker? _inlineWorker;

    /// <summary>
    /// Last index claimed by a worker.
    /// </summary>
    private int _next = -1;

    /// <summary>
    /// Per-slot "result ready" signals (multi-worker only).
    /// </summary>
    private readonly SemaphoreSlim[]? _ready;

    /// <summary>
    /// Ring of result slots; index i uses slot i % length (multi-worker only).
    /// </summary>
    private readonly TResult?[]? _results;

    /// <summary>
    /// Cancelled to stop the workers: by the caller's token, the consumer's dispose, or a worker failure.
    /// </summary>
    private readonly CancellationTokenSource? _stop;

    /// <summary>
    /// The work for one index.
    /// </summary>
    private readonly Func<int, ChunkWorker, TResult?, TResult> _work;

    /// <summary>
    /// First exception thrown by a worker, rethrown to the consumer.
    /// </summary>
    private Exception? _workerFailure;

    /// <summary>
    /// Worker tasks; null once disposed or when running inline.
    /// </summary>
    private Task[]? _workers;

    #endregion
}
