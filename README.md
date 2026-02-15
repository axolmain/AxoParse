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

## Features

- **XML and JSON output** — converts BinXml directly to XML or UTF-8 JSON (no xml-to-json conversion)
- **Multi-threaded** — parallel chunk processing with configurable thread count
- **Template pre-compilation** — compiles BinXml templates once for zero repeated overhead
- **WEVT template cache** — extracts templates from Windows PE binaries (DLL/EXE) for offline parsing
- **CRC32 validation** — optional checksum verification on chunks
- **Resilient** — gracefully handles corrupted records, bad checksums, and malformed chunks
- **Minimal allocations** — span-based zero-copy parsing, stack allocations, and value types throughout

## Usage

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

| Parameter | Default | Description |
|---|---|---|
| `maxThreads` | `0` | Thread count. `0`/`-1` = all cores, `1` = single-threaded |
| `format` | `Xml` | `OutputFormat.Xml` or `OutputFormat.Json` |
| `validateChecksums` | `false` | Skip chunks that fail CRC32 validation |
| `wevtCache` | `null` | Pre-built template cache from provider PE binaries |

## Installation

```
dotnet add package AxoParse.Evtx
```

## License

[MIT](LICENSE)
