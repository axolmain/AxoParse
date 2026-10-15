# AxoParse.Benchmarks

BenchmarkDotNet suite for the parser's internals. This is separate from `perf/AxoParse.Bench`, the AOT CLI that
`tools/bench` uses for comparisons against other EVTX parsers.

## Suites

| Class | Category | Measures | Unit |
|---|---|---|---|
| `FileParseBenchmarks` | File | `EvtxParser.Parse` end to end, by file × format × threads (1 / all) | per file |
| `ChunkBenchmarks` | Chunk | `Structure` (header + templates + record walk), `XmlCold`/`JsonCold` (fresh template cache), `XmlWarm`/`JsonWarm` (pre-warmed cache) | per 64 KB chunk |
| `FormatterBenchmarks` | Micro | FILETIME, SYSTEMTIME, GUID, SID, hex, XML/JSON escaping, CRC32 over 64 KB | per value |

`Cold − Warm` is the cost of compiling templates. `Warm − Structure` is the cost of rendering BinXml.

## Running

```powershell
# Everything (slow: the File suite alone is 12 cases)
pwsh tools/bench/bdn-run.ps1 all

# One suite, short job for exploring
pwsh tools/bench/bdn-run.ps1 explore -Filter '*ChunkBenchmarks*'

# Baseline -> change -> candidate -> compare (use Medium when the result decides keep/revert)
pwsh tools/bench/bdn-run.ps1 baseline  -Filter '*ChunkBenchmarks.XmlWarm*' -Job Medium
pwsh tools/bench/bdn-run.ps1 candidate -Filter '*ChunkBenchmarks.XmlWarm*' -Job Medium
pwsh tools/bench/bdn-compare.ps1 perf/results/baseline perf/results/candidate

# Include the JIT disassembly of the benchmarked methods
pwsh tools/bench/bdn-run.ps1 asm -Filter '*FormatterBenchmarks.Sid*' -Disasm
```

Results go to `perf/results/<name>/` (git-ignored). `bdn-compare.ps1` calls a change real only when the mean
moves by more than 3% **and** the 99.9% confidence intervals don't overlap. Any change in allocated bytes
counts. It exits with 1 if anything regressed.

Raw `dotnet run -c Release --project perf/AxoParse.Benchmarks -- --filter ...` works too; all BenchmarkDotNet
console arguments are supported.

## Adding benchmarks

- Load samples with `BenchData.Load("file.evtx")` (relative to `tests/data`) inside `[GlobalSetup]`, never in the benchmark.
- Return a value derived from the work so the JIT can't eliminate it.
- For per-item numbers, loop inside the benchmark and set `OperationsPerInvoke` to a `const` count.
- Internals are visible (`InternalsVisibleTo`), so you can benchmark `internal` primitives directly.
