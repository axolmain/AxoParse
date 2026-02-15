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


| File                               | C# (1 thread)     | C# (8 threads)   | evtx (Rust - 1 thread) | evtx (Rust - 8 threads) | JS Node           | libevtx (C)       |
|------------------------------------|-------------------|------------------|------------------------|-------------------------|-------------------|-------------------|
| 30M security_big_sample.evtx (XML) | 119.3 ms ± 3.4 ms | 71.9 ms ± 0.6 ms | 302.3 ms ± 1.7 ms      | 126.2 ms ± 83.3 ms      | 1.947 s ± 0.017 s | 2.697 s ± 0.029 s |

**Note**: Numbers shown are `real-time` measurements (wall-clock time for invocation to complete). Single-run entries are marked with *(ran once)* — these parsers are too slow for repeated benchmarking via hyperfine.

## Web Demo (Blazor WASM)

Pre-req (optional if you don't want to test the blazor server locally):
- [dotnet-serve](https://github.com/natemcmaster/dotnet-serve)

`demos/AxoParse.Web` is a Blazor WebAssembly app that parses `.evtx` files entirely in-browser — no server required.


```bash
# Dev (interpreted, slower)
dotnet run --project AxoParse.Web

# AOT-compiled (matches benchmark performance)
dotnet publish demos/AxoParse.Web -c Release -o demos/AxoParse.Web/publish
dotnet serve -d ./demos/AxoParse.Web/publish/wwwroot -p 5271
```

> `dotnet run` uses the IL interpreter. For full performance, publish with AOT and serve the static output.

## Web Demo (React + WASM)

`demos/AxoParse.React` is a React + TypeScript app that loads the AxoParse WASM module and renders parsed events with TanStack Table.

```bash
# 1. Build the WASM module
dotnet publish src/AxoParse.Browser -c Release -o demos/AxoParse.React/public/wasm

# 2. Install deps & start dev server
cd demos/AxoParse.React
npm install
npm run dev
```

> The dev server sets `Cross-Origin-Embedder-Policy` and `Cross-Origin-Opener-Policy` headers required by the .NET WASM runtime.

## Installation

tbd

## License

[MIT](LICENSE)
