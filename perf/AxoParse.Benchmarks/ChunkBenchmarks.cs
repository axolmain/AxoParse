using AxoParse.Evtx.BinXml;
using AxoParse.Evtx.Evtx;
using BenchmarkDotNet.Attributes;

namespace AxoParse.Benchmarks;

/// <summary>
/// Per-chunk benchmarks, single-threaded, reported per 64 KB chunk via <c>OperationsPerInvoke</c>.
/// Separates the three cost centres of chunk parsing:
/// structure (header + template preload + record walk), cold-cache rendering (includes
/// template compilation) and warm-cache rendering (steady-state BinXml substitution).
/// </summary>
[BenchmarkCategory("Chunk")]
public class ChunkBenchmarks
{
    #region Properties

    /// <summary>
    /// Sample file under <c>tests/data</c> whose chunks are parsed.
    /// </summary>
    [Params("security.evtx", "sysmon.evtx")]
    public string File { get; set; } = "";

    #endregion

    #region Public Methods

    /// <summary>
    /// Loads the file, collects offsets of the first <see cref="ChunksPerInvoke"/> chunks carrying the <c>ElfChnk\0</c> magic,
    /// and pre-warms the compiled template caches used by the warm-cache benchmarks.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _data = BenchData.Load(File);
        EvtxFileHeader header = EvtxFileHeader.ParseEvtxFileHeader(_data);
        int chunkCount = (_data.Length - header.HeaderBlockSize) / EvtxChunk.ChunkSize;

        List<int> offsets = new(ChunksPerInvoke);
        for (int i = 0; (i < chunkCount) && (offsets.Count < ChunksPerInvoke); i++)
        {
            int offset = header.HeaderBlockSize + i * EvtxChunk.ChunkSize;
            if (_data.AsSpan(offset, 8).SequenceEqual("ElfChnk\0"u8))
                offsets.Add(offset);
        }

        _chunkOffsets = offsets.ToArray();
        if (_chunkOffsets.Length != ChunksPerInvoke)
            throw new InvalidOperationException(
                $"{File} has only {_chunkOffsets.Length} valid chunks; ChunksPerInvoke requires {ChunksPerInvoke}.");

        _warmXmlCache = new Dictionary<Guid, CompiledTemplate?>();
        _warmJsonCache = new Dictionary<Guid, CompiledJsonTemplate?>();
        for (int i = 0; i < _chunkOffsets.Length; i++)
        {
            EvtxChunk.Parse(_data, _chunkOffsets[i], _warmXmlCache, null, OutputFormat.Xml);
            EvtxChunk.Parse(_data, _chunkOffsets[i], null, _warmJsonCache, OutputFormat.Json);
        }
    }

    /// <summary>
    /// Header, template preload and record walk only — no BinXml rendering.
    /// </summary>
    /// <returns>Total records found, returned so the JIT cannot eliminate the work.</returns>
    [Benchmark(OperationsPerInvoke = ChunksPerInvoke)]
    public int Structure()
    {
        int total = 0;
        for (int i = 0; i < _chunkOffsets.Length; i++)
            total += EvtxChunk.Parse(_data.AsSpan(_chunkOffsets[i], EvtxChunk.ChunkSize), _chunkOffsets[i]).Records.Count;
        return total;
    }

    /// <summary>
    /// XML rendering with an empty template cache per chunk, so every template is compiled.
    /// The gap between this and <see cref="XmlWarm"/> is the template-compilation cost.
    /// </summary>
    /// <returns>Total records rendered.</returns>
    [Benchmark(OperationsPerInvoke = ChunksPerInvoke)]
    public int XmlCold()
    {
        int total = 0;
        for (int i = 0; i < _chunkOffsets.Length; i++)
            total += EvtxChunk.Parse(_data, _chunkOffsets[i], new Dictionary<Guid, CompiledTemplate?>(), null, OutputFormat.Xml).Records.Count;
        return total;
    }

    /// <summary>
    /// XML rendering with a pre-warmed template cache — steady-state per-record substitution cost.
    /// </summary>
    /// <returns>Total records rendered.</returns>
    [Benchmark(OperationsPerInvoke = ChunksPerInvoke)]
    public int XmlWarm()
    {
        int total = 0;
        for (int i = 0; i < _chunkOffsets.Length; i++)
            total += EvtxChunk.Parse(_data, _chunkOffsets[i], _warmXmlCache, null, OutputFormat.Xml).Records.Count;
        return total;
    }

    /// <summary>
    /// JSON rendering with an empty template cache per chunk.
    /// </summary>
    /// <returns>Total records rendered.</returns>
    [Benchmark(OperationsPerInvoke = ChunksPerInvoke)]
    public int JsonCold()
    {
        int total = 0;
        for (int i = 0; i < _chunkOffsets.Length; i++)
            total += EvtxChunk.Parse(_data, _chunkOffsets[i], null, new Dictionary<Guid, CompiledJsonTemplate?>(), OutputFormat.Json).Records.Count;
        return total;
    }

    /// <summary>
    /// JSON rendering with a pre-warmed template cache.
    /// </summary>
    /// <returns>Total records rendered.</returns>
    [Benchmark(OperationsPerInvoke = ChunksPerInvoke)]
    public int JsonWarm()
    {
        int total = 0;
        for (int i = 0; i < _chunkOffsets.Length; i++)
            total += EvtxChunk.Parse(_data, _chunkOffsets[i], null, _warmJsonCache, OutputFormat.Json).Records.Count;
        return total;
    }

    #endregion

    #region Fields

    /// <summary>
    /// Number of leading valid chunks parsed per invocation (must be const for <c>OperationsPerInvoke</c>).
    /// Every <see cref="File"/> option has at least this many; setup throws otherwise so per-chunk numbers stay honest.
    /// </summary>
    private const int ChunksPerInvoke = 8;

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Complete EVTX file bytes for the current <see cref="File"/>.
    /// </summary>
    private byte[] _data = [];

    /// <summary>
    /// Absolute file offsets of chunks with valid magic.
    /// </summary>
    private int[] _chunkOffsets = [];

    /// <summary>
    /// XML template cache pre-populated from every chunk during setup.
    /// </summary>
    private Dictionary<Guid, CompiledTemplate?> _warmXmlCache = new();

    /// <summary>
    /// JSON template cache pre-populated from every chunk during setup.
    /// </summary>
    private Dictionary<Guid, CompiledJsonTemplate?> _warmJsonCache = new();

    #endregion
}
