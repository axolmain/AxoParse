# Getting Started

## Install

[Todo] NuGet install command once published:

```
dotnet add package AxoParse.Evtx
```

## Parse an EVTX file

```csharp
using AxoParse.Evtx;

byte[] fileData = File.ReadAllBytes("security.evtx");
EvtxParser parser = EvtxParser.Parse(fileData);

foreach (EvtxChunk chunk in parser.Chunks)
{
    for (int i = 0; i < chunk.Records.Count; i++)
    {
        Console.WriteLine($"Record {chunk.Records[i].EventRecordId}:");
        Console.WriteLine(chunk.ParsedXml[i]);
    }
}
```

## JSON output

Pass `OutputFormat.Json` to get UTF-8 JSON directly (no XML intermediate):

```csharp
EvtxParser parser = EvtxParser.Parse(fileData, format: OutputFormat.Json);

foreach (EvtxChunk chunk in parser.Chunks)
{
    for (int i = 0; i < chunk.Records.Count; i++)
    {
        // ParsedJson contains raw UTF-8 bytes
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(chunk.ParsedJson![i]));
    }
}
```

## Options

| Parameter           | Default | Description                                               |
|---------------------|---------|-----------------------------------------------------------|
| `maxThreads`        | `0`     | Thread count. `0`/`-1` = all cores, `1` = single-threaded |
| `format`            | `Xml`   | `OutputFormat.Xml` or `OutputFormat.Json`                 |
| `validateChecksums` | `false` | Skip chunks that fail CRC32 validation                    |
| `wevtCache`         | `null`  | Pre-built template cache from provider PE binaries        |

## WEVT template cache

For richer event data, pre-load templates from Windows provider DLLs:

```csharp
WevtCache cache = new WevtCache();
cache.AddFromDirectory(@"C:\Windows\System32", "*.dll");

EvtxParser parser = EvtxParser.Parse(fileData, wevtCache: cache);
```

This extracts WEVT_TEMPLATE resources from PE binaries so templates are compiled before any records are parsed.
