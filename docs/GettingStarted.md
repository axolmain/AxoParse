# Getting Started

## Install

```
dotnet add package AxoParse.Evtx
```

## Parse an EVTX file

```csharp
using AxoParse.Evtx.Evtx;

byte[] fileData = File.ReadAllBytes("security.evtx");
EvtxParser parser = EvtxParser.Parse(fileData);

foreach (EvtxEvent evt in parser.GetEvents())
{
    if (evt.IsSuccess)
        Console.WriteLine(evt.Xml);
    else
        Console.WriteLine($"Record {evt.Record.EventRecordId} failed: {evt.Diagnostic}");
}
```

## JSON output

Pass `OutputFormat.Json` to get UTF-8 JSON directly (no XML intermediate):

```csharp
EvtxParser parser = EvtxParser.Parse(fileData, format: OutputFormat.Json);

foreach (EvtxEvent evt in parser.GetEvents())
{
    // Json is a slice of the chunk's UTF-8 output buffer
    Console.WriteLine(System.Text.Encoding.UTF8.GetString(evt.Json.Span));
}
```

## Stream output to a file or stdout

When you only need the rendered output, `WriteTo` is the fastest path. Given a path (or an open `SafeFileHandle`), each
worker reads the 64 KB chunk it's about to render, renders it into a reusable UTF-8 buffer, and the chunks are written
to the stream in order. The file is never loaded whole, so memory stays at a few MB whatever its size (files over 2 GB
work), and nothing is retained per record. The bytes are identical to concatenating `Parse` output.

```csharp
using FileStream output = File.Create("security.json");
int written = EvtxParser.WriteTo("security.evtx", output, OutputFormat.Json);
```

If the file is already in memory, `WriteTo(byte[] fileData, ...)` takes the bytes instead, with the same output.

## Consume events as they're parsed

`ParseAsync` parses chunks in parallel and yields events in file order as soon as each chunk is done, so you can start
processing before the whole file is parsed:

```csharp
await foreach (EvtxEvent evt in EvtxParser.ParseAsync(fileData, cancellationToken: token))
{
    if (evt.IsSuccess)
        Console.WriteLine(evt.Xml);
}
```

## Direct chunk access

For hot paths, skip `GetEvents()` and read the chunk's output directly. `ParsedUtf8` holds each record's rendered
bytes (XML or JSON) without decoding to `string`. `ParsedXml` decodes lazily on first access per record.

```csharp
using Stream stdout = Console.OpenStandardOutput();

foreach (EvtxChunk chunk in parser.Chunks)
{
    for (int i = 0; i < chunk.Records.Count; i++)
        stdout.Write(chunk.ParsedUtf8[i].Span);
}
```

## Options

| Parameter           | Default | Description                                               |
|---------------------|---------|-----------------------------------------------------------|
| `maxThreads`        | `0`     | Thread count. `0`/`-1` = all cores, `1` = single-threaded |
| `format`            | `Xml`   | `OutputFormat.Xml` or `OutputFormat.Json`                 |
| `validateChecksums` | `false` | Skip chunks that fail CRC32 validation                    |
| `wevtCache`         | `null`  | Provider templates used when a record's embedded template is missing or corrupt |
| `salvage`           | `false` | Keep the well-formed part of a record whose BinXml is corrupt instead of failing it |

`WriteTo` takes the same options (`wevtCache` and then `salvage` come last, after the cancellation token). On `Parse`
and `ParseAsync`, `salvage` comes after the cancellation token.

## WEVT template cache

`WevtCache` is a **fallback** for damaged logs, as in the Rust `evtx` crate. When a record's embedded template is
missing (size 0), runs past its chunk, or won't compile, the record is rendered with the provider template of the
same GUID instead. A usable embedded template is never replaced, so passing a cache doesn't change output for an
intact log.

Load templates from Windows provider DLLs:

```csharp
using AxoParse.Evtx.Wevt;

WevtCache cache = new WevtCache();
cache.AddFromDirectory(@"C:\Windows\System32", "*.dll");

EvtxParser parser = EvtxParser.Parse(fileData, wevtCache: cache);
```

This extracts and compiles the WEVT_TEMPLATE resources in each PE binary, keyed by template GUID. The same cache works
for XML and JSON output, and with `Parse`, `ParseAsync` and `WriteTo`, and can be shared across threads and calls.
