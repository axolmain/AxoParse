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
  <!-- <a href="https://www.nuget.org/packages/AxoParse.Evtx">
    <img src="https://img.shields.io/nuget/v/AxoParse.Evtx.svg?style=flat-square"
      alt="NuGet version" />
  </a> -->
  <!-- NuGet downloads -->
  <!-- <a href="https://www.nuget.org/packages/AxoParse.Evtx">
    <img src="https://img.shields.io/nuget/dt/AxoParse.Evtx.svg?style=flat-square"
      alt="NuGet downloads" />
  </a> -->
  <!-- License -->
  <a href="LICENSE">
    <img src="https://img.shields.io/github/license/axolmain/AxoParse.svg?style=flat-square"
      alt="License" />
  </a>
</div>

<br />

## A few words :)

This project was inspired by work I did a few years ago as an intern, and by omerbenamram's [Rust-based parser](https://github.com/omerbenamram/evtx/tree/master). 
I wanted to build an evtx parser that was faster than the one which uses the default Windows API, and ideally become the 
fastest open source evtx parser. So after reading through the fantastic documentation in the [libevtx](https://github.com/libyal/libevtx/blob/main/documentation/Windows%20XML%20Event%20Log%20(EVTX).asciidoc#name) 
project, and a few ai searches - I came up with a rough outline of a parser and then optimized it as best I can to get this. 

Much of my documentation/comments are inspired or taken from the libevtx, and Microsoft's docs, and much of the strategy 
for how to handle errors were inspired by omerbenamram. 

That all being said, I write this at 03:43 AM as caffeine and adderall wear off so if there are odd things in the codebase 
you see before I get to it, please do leave a GH issue. Feel free to contribute too! I'll leave instructions and such on 
that another day..

## Features

- **XML and JSON output** — converts BinXml directly to XML or UTF-8 JSON (no xml-to-json conversion)
- **Multi-threaded** — parallel chunk processing with configurable thread count
- **Template pre-compilation** — compiles BinXml templates once for zero repeated overhead
- **WEVT template cache** — extracts templates from Windows PE binaries (DLL/EXE) for offline parsing
- **CRC32 validation** — optional checksum verification on chunks
- **Resilient** — gracefully handles corrupted records, bad checksums, and malformed chunks
- **Minimal allocations** — span-based zero-copy parsing, stack allocations, and value types throughout

## Usage 
> or rather how I expect this to be used - not on nuget yet

```csharp
using AxoParse.Evtx;

byte[] fileData = File.ReadAllBytes("security.evtx");

EvtxParser parser = EvtxParser.Parse(fileData, maxThreads: 0, format: OutputFormat.Xml);

foreach (EvtxChunk chunk in parser.Chunks)
{
    for (int i = 0; i < chunk.Records.Count; i++)
    {
        Console.WriteLine($"Record {chunk.Records[i].EventRecordId}:");
        Console.WriteLine(chunk.ParsedXml[i]);
    }
}
```

### With WEVT template cache

```csharp
WevtCache cache = new WevtCache();
cache.AddFromDirectory(@"C:\Windows\System32", "*.dll");

EvtxParser parser = EvtxParser.Parse(fileData, wevtCache: cache);
```

### Options

| Parameter           | Default | Description                                               |
|---------------------|---------|-----------------------------------------------------------|
| `maxThreads`        | `0`     | Thread count. `0`/`-1` = all cores, `1` = single-threaded |
| `format`            | `Xml`   | `OutputFormat.Xml` or `OutputFormat.Json`                 |
| `validateChecksums` | `false` | Skip chunks that fail CRC32 validation                    |
| `wevtCache`         | `null`  | Pre-built template cache from provider PE binaries        |

## Benchmark Results

I'm running tests on a 2025 Macbook Air with an M4 chip and benching used hyperfine (statistical measurements tool).

Bench run: February 2026.

System: macOS Sequoia - Version 15.7.3

Benchmark commit: f0f1fe6.

Libraries benched:

- [libevtx](https://github.com/libyal/libevtx) - C-based
- [evtx](https://github.com/omerbenamram/evtx) - Rust-based
- [evtx-parser](https://github.com/axolmain/evtx-parser) - Typescript-based (technically JS ig but hey)


| File                                | C# (1 thread)     | C# (8 threads)    | C# WASM           | evtx (Rust - 1 thread) | evtx (Rust - 8 threads) | JS Node           | libevtx (C)       |
|-------------------------------------|-------------------|-------------------|-------------------|------------------------|-------------------------|-------------------|-------------------|
| 30M security_big_sample.evtx (XML)  | 105.3 ms ± 5.4 ms | 85.6 ms ± 2.4 ms  | N/A (web only)    | 157.4 ms ± 1.2 ms      | 57.3 ms ± 1.7 ms        | 1.013 s ± 0.013 s | 1.422 s ± 0.055 s |
| 30M security_big_sample.evtx (JSON) | 267.2 ms ± 3.5 ms | 134.1 ms ± 1.9 ms | 115.7 ms ± 1.4 ms | 146.4 ms ± 7.2 ms      | 53.6 ms ± 1.4 ms        | 1.337 s ± 0.010 s | No support        |

## Documentation

- [Getting Started](docs/GettingStarted.md) — install, usage examples, options
- [Architecture](docs/Architecture.md) — parsing pipeline, threading model, template compilation
- [EVTX Format](docs/EvtxFormat.md) — binary format primer (headers, chunks, records, BinXml)
- [Error Handling](docs/ErrorHandling.md) — `EvtxParseException`, error codes, corruption resilience
- [Performance](docs/Performance.md) — threading, memory, XML vs JSON
- [WASM Integration](docs/WasmIntegration.md) — using the WASM build in browser apps
- [Contributing](docs/Contributing.md) — build, test, code style, project structure

## License

[MIT](LICENSE)
