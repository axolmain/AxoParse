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

### Golden output

`GoldenOutputTests` locks the exact output of every sample in `tests/data`. It stores two SHA-256s per sample and format
in `tests/AxoParse.Evtx.Tests/Golden/output-sha256.txt` and checks both at 1 and 8 threads: `<format>/<sample>` hashes
the events' rendered bytes (`EvtxEvent.Utf8` from `Parse`, concatenated), and `framed/<format>/<sample>` hashes `WriteTo`
output (the same bytes with a line feed after each record). A rendering change moves both; a framing change moves only
the `framed/` lines, which is how a framing change proves it left the record bytes alone. Any byte change fails the test. The failure message lists the affected sample and format, and the path of a
temp folder holding the new output so you can diff it against output from a known-good build.

If the change is intended, re-bless and commit the updated hash file with your change:

```bash
AXOPARSE_REBLESS=1 dotnet test --project tests/AxoParse.Evtx.Tests --filter-class "AxoParse.Evtx.Tests.GoldenOutputTests"
```

On Windows PowerShell: `$env:AXOPARSE_REBLESS=1; dotnet test ...; Remove-Item Env:AXOPARSE_REBLESS`.

Equality tests also check that `Parse(path)`, `ReadEvents`, `ReadEventsAsync` and every `WriteTo` overload produce
exactly what `Parse(byte[])` produces, for every sample, format and thread count, with checksum validation off and on.

### Corruption test

`CorruptionFuzzTests` mutates every sample 100 times with a fixed seed: bit flips, random overwrites, zeroed ranges,
truncation, and targeted hits on file, chunk and record header fields. It asserts that `Parse` and `WriteTo` never throw
anything except `EvtxParseException`. A failure lists the sample, round, seed and mutation, so you can reproduce it by
running that round again. Before a release, run it longer:

```bash
AXOPARSE_FUZZ_ROUNDS=5000 dotnet test --project tests/AxoParse.Evtx.Tests --filter-class "AxoParse.Evtx.Tests.CorruptionFuzzTests"
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
  AxoParse.CliHash/         # Checks a built CLI's output against the golden hashes
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

# Check a published AOT CLI's output against the golden hashes (bench-all.sh runs this before timing)
dotnet publish perf/AxoParse.Bench -c Release -o perf/AxoParse.Bench/bin/publish
dotnet run --project perf/AxoParse.CliHash -c Release -- perf/AxoParse.Bench/bin/publish/AxoParse.Bench
```

On Windows, pass `AxoParse.Bench.exe`. `AxoParse.CliHash` and the BenchmarkDotNet scripts (`pwsh`) work on every
platform. `bench-all.sh` is bash: use WSL or Git Bash.

## Pull requests

Log an issue then create a PR for any change :)