using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Order;

namespace AxoParse.Benchmarks;

/// <summary>
/// Shared BenchmarkDotNet configuration applied to every benchmark in this assembly.
/// Always collects allocation data and exports full JSON so <c>tools/bench/bdn-compare.ps1</c>
/// can diff a baseline run against a candidate run. Job selection (short/medium/default),
/// filters and the disassembly diagnoser are left to command-line arguments.
/// </summary>
internal static class BenchConfig
{
    #region Public Methods

    /// <summary>
    /// Builds on the default config (loggers, columns, validators) and adds the memory diagnoser,
    /// the full JSON exporter, a median column and a single joined summary.
    /// </summary>
    /// <returns>The config passed to <c>BenchmarkSwitcher</c>.</returns>
    public static IConfig Create() =>
        ManualConfig.Create(DefaultConfig.Instance)
                    .AddDiagnoser(MemoryDiagnoser.Default)
                    .AddExporter(JsonExporter.Full)
                    .AddColumn(StatisticColumn.Median)
                    .WithOrderer(new DefaultOrderer(SummaryOrderPolicy.Declared))
                    .WithOptions(ConfigOptions.JoinSummary);

    #endregion
}
