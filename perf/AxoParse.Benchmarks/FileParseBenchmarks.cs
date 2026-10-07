using AxoParse.Evtx.Evtx;
using BenchmarkDotNet.Attributes;

namespace AxoParse.Benchmarks;

/// <summary>
/// End-to-end benchmarks for <see cref="EvtxParser.Parse"/>: file header, chunk scan,
/// template compilation and BinXml rendering for every record. This is the number users feel.
/// Single-threaded runs isolate per-core cost; all-cores runs expose contention and scaling.
/// </summary>
[BenchmarkCategory("File")]
public class FileParseBenchmarks
{
    #region Properties

    /// <summary>
    /// Sample file under <c>tests/data</c>. Mix of a large Security log (template-reuse heavy),
    /// a mid-sized Security log, and Sysmon (wide EventData payloads).
    /// </summary>
    [Params("security.evtx", "sysmon.evtx", "benchmark/security_big_sample.evtx")]
    public string File { get; set; } = "";

    /// <summary>
    /// Output format rendered for each record.
    /// </summary>
    [Params(OutputFormat.Xml, OutputFormat.Json)]
    public OutputFormat Format { get; set; }

    /// <summary>
    /// Thread count passed to the parser: 1 = single-threaded, 0 = all cores.
    /// </summary>
    [Params(1, 0)]
    public int Threads { get; set; }

    #endregion

    #region Public Methods

    /// <summary>
    /// Loads the sample file once per parameter combination so file I/O is excluded from measurement.
    /// </summary>
    [GlobalSetup]
    public void Setup() => _data = BenchData.Load(File);

    /// <summary>
    /// Parses the full file and renders every record in the selected format.
    /// </summary>
    /// <returns>Total record count, returned so the JIT cannot eliminate the work.</returns>
    [Benchmark]
    public int Parse() => EvtxParser.Parse(_data, Threads, Format).TotalRecords;

    #endregion

    #region Non-Public Fields

    /// <summary>
    /// Complete EVTX file bytes for the current <see cref="File"/>.
    /// </summary>
    private byte[] _data = [];

    #endregion
}
