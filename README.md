<h1 align="center">AxoParse.Evtx</h1>
<div align="center">
  <p>
    <strong>
      High-performance .NET library for parsing Windows Event Log (.evtx) files
    </strong>
  </p>
</div>

<br />

<div align="center">
  <!-- NuGet version -->
  <a href="https://www.nuget.org/packages/AxoParse.Evtx">
    <img src="https://img.shields.io/nuget/v/AxoParse.Evtx.svg?style=flat-square"
      alt="NuGet version" />
  </a>
  <!-- NuGet downloads -->
  <a href="https://www.nuget.org/packages/AxoParse.Evtx">
    <img src="https://img.shields.io/nuget/dt/AxoParse.Evtx.svg?style=flat-square"
      alt="NuGet downloads" />
  </a>
  <!-- License -->
  <a href="LICENSE">
    <img src="https://img.shields.io/github/license/axolmain/AxoParse.svg?style=flat-square"
      alt="License" />
  </a>
</div>

<br />

## Background

This project was inspired by work I did as an intern a few years ago and by
omerbenamram's [Rust-based parser](https://github.com/omerbenamram/evtx/tree/master).
The goal was to build an EVTX parser faster than the default Windows API — and ideally the fastest open-source
option available. So after reading through the fantastic documentation
in [libevtx](https://github.com/libyal/libevtx/blob/main/documentation/Windows%20XML%20Event%20Log%20(EVTX).asciidoc#name)
project, and a few ai searches - I came up with a rough outline of a parser and then optimized it as best I can to get
this.

Much of my documentation/comments are inspired or taken from the libevtx, and Microsoft's docs, and much of the strategy
for how to handle errors were inspired by omerbenamram.

If you find issues, please open a [GitHub issue](https://github.com/axolmain/AxoParse/issues). Contributions welcome!

## AI Disclosure

Heads up: a fair bit of this project was built with help from Claude (via Claude Code), especially the performance
work, tests, benchmark tooling, and docs. I also use it to write commit messages, since it writes far more detail than
I'd ever bother to, which keeps the history really clean to track. Commits with significant AI involvement carry a
`Co-Authored-By` trailer. Everything gets reviewed by me and has to pass the test suite and benchmarks before it lands.

## Features

- **XML and JSON output** — renders BinXml straight to UTF-8 XML or JSON (no xml-to-json conversion, no UTF-16 detour)
- **Streaming** — `ReadEvents` / `ReadEventsAsync` stream events and `WriteTo` writes JSON Lines or XML to any
  `Stream`, reading the file chunk by chunk; memory stays at a few MB whatever the file size
- **One options record** — `EvtxOptions` is immutable and shareable; every entry point is thread-safe
- **Multi-threaded** — parallel chunk processing with configurable thread count
- **Template pre-compilation** — compiles BinXml templates once for zero repeated overhead
- **WEVT fallback templates** — `WevtCache` reads templates from Windows provider PE binaries (DLL/EXE) and uses them
  to render records whose embedded template is missing or corrupt (carved and damaged logs)
- **CRC32 validation** — optional checksum verification on chunks
- **Resilient** — a corrupt record fails on its own with a typed `EvtxDiagnostic` (strict, like the Rust `evtx` crate),
  or keeps its well-formed part with `Salvage = true`; bad checksums and damaged chunks don't stop the rest of the file
- **Minimal allocations** — span-based zero-copy parsing, stack allocations, and value types throughout
- **Culture-invariant** — output is identical regardless of the machine's locale

## Install

```
dotnet add package AxoParse.Evtx
```

## Usage

```csharp
using AxoParse.Evtx;

EvtxLog log = EvtxParser.Parse("security.evtx");

foreach (EvtxEvent evt in log.Events)
{
    if (evt.IsSuccess)
        Console.WriteLine($"{evt.RecordId} {evt.WrittenTimeUtc:O} {evt.GetText()}");
}
```

### Stream straight to a file

The fastest way to export. The file is read chunk by chunk as it's rendered (it's never loaded whole), nothing is kept
per record, and every record is written on its own line (JSON Lines for JSON) with the same bytes `Parse` renders.

```csharp
using FileStream output = File.Create("security.jsonl");
EvtxParser.WriteTo("security.evtx", output, new EvtxOptions { Format = OutputFormat.JsonStructured });
```

### Consume events as they're parsed

```csharp
await foreach (EvtxEvent evt in EvtxParser.ReadEventsAsync("security.evtx"))
    Console.WriteLine(evt.GetText());
```

### Options

Every entry point takes an optional `EvtxOptions` (an immutable record: `new EvtxOptions { ... }` or `with`).

| Property            | Default | Description                                                                          |
|---------------------|---------|--------------------------------------------------------------------------------------|
| `Format`            | `Xml`   | `OutputFormat.Xml` or `OutputFormat.JsonStructured`                                  |
| `MaxThreads`        | `0`     | Worker threads. `0` = all cores, `1` = single-threaded                               |
| `ValidateChecksums` | `false` | Skip chunks that fail CRC32 validation (reported as diagnostics)                     |
| `Salvage`           | `false` | Keep the well-formed part of a record whose BinXml is corrupt instead of failing it |
| `WevtCache`         | `null`  | Provider templates used when a record's embedded template is missing or corrupt      |
| `XmlEventsRoot`     | `false` | `WriteTo` with XML: wrap the records in `<Events>` so the output is one XML document |

## Benchmark Results

Measured with [hyperfine](https://github.com/sharkdp/hyperfine) (5 warmup runs, 10 measured) on a 2025 MacBook Air
(Apple M4, macOS 26.6), October 2026. AxoParse commit `0972526`; Rust `evtx` at upstream `7479d02`. Reproduce with
`tools/bench/bench-all.sh` (see `tools/bench/scripts/README.md`).

Libraries benched:

- [evtx](https://github.com/omerbenamram/evtx) - Rust-based
- [libevtx](https://github.com/libyal/libevtx) - C-based
- [evtx-parser](https://github.com/axolmain/evtx-parser) - TypeScript-based (run on Node)

| 30 MB `security_big_sample.evtx` | XML               | JSON              |
|----------------------------------|-------------------|-------------------|
| **C# (1 thread)**                | 36.1 ms ± 2.4 ms  | 40.0 ms ± 0.9 ms  |
| **C# (8 threads)**               | 16.4 ms ± 0.7 ms  | 18.6 ms ± 0.8 ms  |
| evtx (Rust, 1 thread)            | 38.5 ms ± 0.3 ms  | 37.4 ms ± 0.3 ms  |
| evtx (Rust, 8 threads)           | 9.7 ms ± 0.3 ms   | 9.3 ms ± 0.4 ms   |
| JS Node                          | 930.7 ms ± 13 ms  | 1.129 s ± 0.011 s |
| libevtx (C, 1 thread)            | 1.396 s ± 0.016 s | No support        |

Single-threaded, AxoParse and Rust `evtx` are neck and neck. With 8 threads, Rust is currently 1.7–2× faster.

WASM builds on Node (not like-for-like, so listed separately):

| WASM, JSON                                                    | Time              |
|---------------------------------------------------------------|-------------------|
| C# WASM (`ParseEvtxFile` + `GetRecordPage`, with field extraction) | 1.165 s ± 0.005 s |
| Rust `evtx-wasm` (Arrow IPC per chunk; no JSON output upstream) | 748.4 ms ± 3.8 ms |

## Documentation

- [Release Notes & API Reference](docs/ReleaseNotes.md) — release notes, full public API docs
- [Getting Started](docs/GettingStarted.md) — install, usage examples, options
- [Architecture](docs/Architecture.md) — parsing pipeline, threading model, template compilation
- [EVTX Format](docs/EvtxFormat.md) — binary format primer (headers, chunks, records, BinXml)
- [Error Handling](docs/ErrorHandling.md) — `EvtxParseException`, error codes, corruption resilience
- [Performance](docs/Performance.md) — choosing an entry point, threading, memory, benchmarks
- [WASM Integration](docs/WasmIntegration.md) — using the WASM build in browser apps
- [Contributing](docs/Contributing.md) — build, test, code style, project structure

## License

[MIT](LICENSE)
