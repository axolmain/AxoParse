# Contributing

## Build

```bash
dotnet build
```

Requires .NET 10 SDK.

## Run tests

```bash
# All tests
dotnet test --project tests/AxoParse.Evtx.Tests

# Filter by class
dotnet test --project tests/AxoParse.Evtx.Tests --filter-class "AxoParse.Evtx.Tests.EvtxFileHeaderTests"

# Filter by method
dotnet test --project tests/AxoParse.Evtx.Tests --filter-method "*ThrowsOnTruncatedData*"
```

## Code style

- **Explicit types** — avoid `var` unless required (anonymous types)
- **Value types** — prefer `struct`, `Span<T>`, `ReadOnlySpan<T>`, `stackalloc` over heap allocations
- **No LINQ in hot paths** — simple loops are faster and allocate less
- **Enums/const/readonly** over magic literals
- **No unnecessary abstractions** — if a loop is faster than a helper, use the loop

## Comments

Document all public and internal members with XML doc comments. This is a binary format parser — contributors need to understand EVTX specifics without external references.

```csharp
/// <summary>
/// Brief description.
/// </summary>
/// <param name="paramName">Parameter description.</param>
/// <returns>Return value description.</returns>
```

Inline comments only for non-obvious logic (bit manipulations, format-specific offsets). If the code is self-explanatory, don't comment it.

## Project structure

```
src/
  AxoParse.Evtx/           # Core parser library
    Evtx/                   # File/chunk/record parsing
    BinXml/                 # BinXml token stream parser + value formatting
    Wevt/                   # WEVT template cache (PE binary extraction)
  AxoParse.Browser/         # WASM interop layer
demos/
  AxoParse.React/           # React demo app (Vite + TypeScript)
tests/
  AxoParse.Evtx.Tests/      # xUnit v3
perf/
  AxoParse.Bench/           # AOT CLI used by tools/bench for cross-parser comparisons
  AxoParse.Benchmarks/      # BenchmarkDotNet suite for internals (file / chunk / formatter)
  profiling/                # Profiling harness
tools/
  bench/                    # hyperfine comparison vs Rust/C/JS parsers, BenchmarkDotNet run/compare scripts
```

## Performance changes

Measure before and after any change that touches a hot path:

```bash
# Internals: baseline -> change -> candidate -> compare (see perf/AxoParse.Benchmarks/README.md)
pwsh tools/bench/bdn-run.ps1 baseline -Filter '*ChunkBenchmarks*' -Job Medium
pwsh tools/bench/bdn-run.ps1 candidate -Filter '*ChunkBenchmarks*' -Job Medium
pwsh tools/bench/bdn-compare.ps1 perf/results/baseline perf/results/candidate

# End to end vs other parsers (see tools/bench/scripts/README.md)
tools/bench/bench-all.sh
```

## Pull requests

Log an issue then create a PR for any change :)