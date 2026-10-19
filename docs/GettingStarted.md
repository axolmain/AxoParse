# Getting Started

## Install

```
dotnet add package AxoParse.Evtx
```

Everything public lives in the `AxoParse.Evtx` namespace.

## Parse an EVTX file

```csharp
using AxoParse.Evtx;

EvtxLog log = EvtxParser.Parse("security.evtx");

foreach (EvtxEvent evt in log.Events)
{
    if (evt.IsSuccess)
        Console.WriteLine(evt.GetText());
    else
        Console.WriteLine($"Record {evt.RecordId} at 0x{evt.FileOffset:X} failed: {evt.Diagnostic?.Message}");
}

Console.WriteLine($"{log.RecordCount} records in {log.Chunks.Count} chunks, {log.Diagnostics.Count} diagnostics");
```

`Parse(path)` reads the file chunk by chunk as it parses, so only the rendered output is held in memory. If the file is
already in memory, `Parse(byte[] fileData)` takes the bytes instead, with the same result.

An `EvtxEvent` is a small view over the chunk that holds its data:

| Member           | Description                                                                              |
|------------------|------------------------------------------------------------------------------------------|
| `RecordId`       | Event record ID from the record header                                                   |
| `WrittenTimeUtc` | When the record was written (record header FILETIME), UTC                                |
| `FileOffset`     | Absolute file offset of the record header                                                |
| `Utf8`           | The rendered event as UTF-8 (`ReadOnlyMemory<byte>`, a slice of the chunk's output)     |
| `GetText()`      | `Utf8` decoded to a new `string`                                                         |
| `Diagnostic`     | `EvtxDiagnostic?`: why the record didn't decode cleanly, or `null`                       |
| `IsSuccess`      | `true` when there is no diagnostic                                                       |
| `IsSalvaged`     | `true` when `Utf8` holds only the part decoded before corruption (`Salvage = true`)      |

## Options

Every entry point takes an optional `EvtxOptions`. It is an immutable record: create one with an object initializer,
derive variants with `with`, and share it across threads and calls.

```csharp
EvtxOptions options = new() { Format = OutputFormat.JsonStructured, MaxThreads = 4 };
EvtxLog log = EvtxParser.Parse("security.evtx", options);
EvtxLog strict = EvtxParser.Parse("security.evtx", options with { ValidateChecksums = true });
```

| Property            | Default | Description                                                                           |
|---------------------|---------|---------------------------------------------------------------------------------------|
| `Format`            | `Xml`   | `OutputFormat.Xml` or `OutputFormat.JsonStructured`                                   |
| `MaxThreads`        | `0`     | Worker threads. `0` (or negative) = all cores, `1` = parse on the calling thread      |
| `ValidateChecksums` | `false` | Skip chunks that fail CRC32 validation; each one is reported in `EvtxLog.Diagnostics` |
| `Salvage`           | `false` | Keep the well-formed part of a record whose BinXml is corrupt instead of failing it  |
| `WevtCache`         | `null`  | Provider templates used when a record's embedded template is missing or corrupt       |
| `XmlEventsRoot`     | `false` | `WriteTo` with XML: wrap the records in `<Events>` so the output is one XML document  |

Output and event order never depend on `MaxThreads`.

## JSON output

`OutputFormat.JsonStructured` renders UTF-8 JSON directly (no XML intermediate). It is a lossless rendering of the XML
tree: each element is an object with `#name`, optional `#attrs` and optional `#content` (child elements and text, in
order).

```csharp
EvtxLog log = EvtxParser.Parse("security.evtx", new EvtxOptions { Format = OutputFormat.JsonStructured });

foreach (EvtxEvent evt in log.Events)
{
    using JsonDocument doc = JsonDocument.Parse(evt.Utf8);
    // ...
}
```

## Stream output to a file or stdout

When you only need the rendered output, `WriteTo` is the fastest path. Each worker reads the 64 KB chunk it's about to
render, renders it into a reusable UTF-8 buffer, and the chunks are written to the stream in order. The file is never
loaded whole, so memory stays at a few MB whatever its size (files over 2 GB work), and nothing is retained per record.

Every record that has output is written followed by a line feed, so JSON output is [JSON Lines](https://jsonlines.org).
XML output is one record after another; an XML record's text can itself contain line breaks, so set `XmlEventsRoot`
to get a single `<Events>` document that an XML parser can read in one go. Record bytes are exactly what `Parse`
renders; a record that fails to render writes nothing.

```csharp
using FileStream output = File.Create("security.jsonl");
int written = EvtxParser.WriteTo("security.evtx", output, new EvtxOptions { Format = OutputFormat.JsonStructured });
```

The input can be a path, a `byte[]`, or any readable, seekable `Stream` holding the file from position 0. A
`FileStream` is read with positional reads through its handle (its position is left alone), so workers read in
parallel; other streams are read one chunk at a time.

## Stream events as they're parsed

`ReadEvents` and `ReadEventsAsync` parse chunks in parallel and yield events in output order as soon as each chunk is
done, holding only a few chunks per worker at a time. Stopping the enumeration early stops the workers and, for a path,
closes the file.

```csharp
foreach (EvtxEvent evt in EvtxParser.ReadEvents("security.evtx"))
{
    if (evt.IsSuccess)
        Console.WriteLine(evt.GetText());
}

await foreach (EvtxEvent evt in EvtxParser.ReadEventsAsync("security.evtx", cancellationToken: token))
    Console.WriteLine(evt.RecordId);
```

They yield exactly the events of `Parse`, in the same order. Chunks skipped for a checksum mismatch are skipped
silently; use `Parse` when you need `EvtxLog.Diagnostics`.

## Single chunks

`ParseChunk` decodes one standalone 64 KB chunk, for carving or for reading a file chunk by chunk (the WASM build does
this). `ParseFileHeader` decodes the first 128 bytes of a file.

```csharp
EvtxFileHeader header = EvtxParser.ParseFileHeader(headerBytes);
EvtxChunk chunk = EvtxParser.ParseChunk(chunkBytes);
foreach (EvtxEvent evt in chunk.Events)
    Console.WriteLine(evt.GetText());
```

Offsets in a standalone chunk are relative to the chunk (`chunk.FileOffset` is 0).

## Diagnostics

Problems that don't stop parsing are reported as `EvtxDiagnostic` values: `Kind`, `RecordId` (null for a chunk),
`FileOffset` and `Message`. `EvtxLog.Diagnostics` lists them all in output order; each event carries its own.

| Kind                    | Meaning                                                                       |
|-------------------------|-------------------------------------------------------------------------------|
| `ChunkChecksumMismatch` | A chunk failed CRC32 validation and was skipped (`ValidateChecksums` only)    |
| `RecordRenderFailed`    | A record's BinXml couldn't be decoded; its output is empty                    |
| `RecordSalvaged`        | A corrupt record kept the output decoded before the corruption (`Salvage`)    |

See [Error Handling](ErrorHandling.md) for the exceptions that do stop parsing.

## WEVT template cache

`WevtCache` is a **fallback** for damaged logs, as in the Rust `evtx` crate. When a record's embedded template is
missing (size 0), runs past its chunk, or won't compile, the record is rendered with the provider template of the
same GUID instead. A usable embedded template is never replaced, so passing a cache doesn't change output for an
intact log.

Load templates from Windows provider DLLs:

```csharp
WevtCache cache = new WevtCache();
cache.AddFromDirectory(@"C:\Windows\System32", "*.dll");

EvtxLog log = EvtxParser.Parse("carved.evtx", new EvtxOptions { WevtCache = cache });
```

This extracts and compiles the WEVT_TEMPLATE resources in each PE binary, keyed by template GUID. The same cache works
for every output format and entry point, and can be shared across threads and calls.

## Thread safety

`EvtxParser` methods share no state, so any number can run at once. `EvtxOptions`, `EvtxLog`, `EvtxChunk` and
`EvtxEvent` are immutable and safe to read from any thread. A `WevtCache` can be shared by concurrent parses; add
templates to it before parsing starts.
