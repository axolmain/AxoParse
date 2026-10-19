# AxoParse.Evtx — Release Notes & API Reference

## Release Notes

### v0.3.0 (unreleased)

**New:**

- **`EvtxParser.WriteTo(string path, ...)` and `WriteTo(SafeFileHandle file, ...)`** stream a file from disk. Each worker
  reads the 64 KB chunk it renders (positional reads, so the handle's position isn't used), so reading overlaps
  rendering and the file is never loaded whole. Output and record count are identical to `WriteTo(byte[], ...)`.
  The benchmark CLI now uses it. On the 30 MB sample (Apple M4), peak memory went from 39 to 8 MiB single-threaded
  and 49 to 18 MiB with 8 threads. Time went from 33.7 to 31.9 ms (XML) and 38.5 to 35.9 ms (JSON) single-threaded,
  and from 13.0 to 11.9 ms (XML) with 8 threads.
- **`salvage: true`** (on `Parse`, `ParseAsync` and `WriteTo`, last parameter) keeps the well-formed part of a record
  whose BinXml is corrupt instead of failing it. Decoding stops at the corruption and every open element is closed, so
  the XML or JSON is still well-formed. `EvtxEvent.Diagnostic` says where it stopped (`BinXml salvaged …`). Off by
  default.

**Fixes:**

- **Entity references with inline names truncated the event.** An entity reference's name (like `quot` in
  `&quot;`) uses the same name structure as an element name, so it can be defined inline. Its inline bytes were
  read as tokens, and everything after the first such entity was lost. 26 records in the two CAPI2 samples and 2 in
  `Archive-ForwardedEvents-test.evtx` were cut short this way (for example, an HTTP header list ended at
  `Via: 1.1 [&quot;`). All records of those three samples now match the Rust `evtx` crate's output.
- **Corrupt records could render invalid JSON while reporting success.** These bugs were found by the corruption test,
  which now requires every non-empty JSON record to parse:
  - A record or template whose bytes held more than one top-level item rendered `{…}{…}` or `{…},…`. A record and a
    template body now each end after their root element.
  - A nested template that rendered nothing left a dangling comma in its parent's `#content`; it now renders `""`.
  - A SYSTEMTIME field out of range for its digits (month 300, say) was written as non-digit bytes, including control
    characters. Such fields are now written in full.

  Intact records are unaffected.

**Changes:**

- **Corrupt BinXml fails the record instead of rendering junk.** Records, embedded BinXml values and templates that
  don't compile are now decoded by one strict decoder shared by XML and JSON, as in the Rust `evtx` crate. An unknown
  token, nesting deeper than 64 levels, a name running past the data, a substitution outside a template, or an
  element inside an attribute value fails the record with a diagnostic. Before, the bytes were skipped and the record
  could render partial or meaningless output (like `<></>`) while reporting success. Use `salvage: true` to keep the
  well-formed part instead. Output for intact records is unchanged apart from the entity fix above.
- JSON for templates that don't compile now matches the compiled path: attribute values are formatted as JSON (they
  were XML-escaped, so `&` came out as `&amp;`), and an empty value in element content renders as `""`.

- **Rendering only reads a record's own chunk.** A template body or substitution value that ran past the end of its
  64 KB chunk used to be read from the next chunk's bytes. Now the template is treated as unavailable or the record
  fails (with a diagnostic), as in the Rust `evtx` crate, which parses each chunk from its own buffer. This only
  affects corrupt files: output for every test sample is unchanged.
- `WriteTo` checks chunk magic and checksums on its worker threads, so `validateChecksums: true` costs less wall time.
- **`WevtCache` now works, as a fallback.** When a record's embedded template is missing (size 0), runs past its chunk,
  or won't compile, the record is rendered with the provider template of the same GUID, in XML and JSON, as in the
  Rust `evtx` crate. A usable embedded template is never replaced (tested: same-GUID provider templates leave an intact
  log's output byte-identical). Provider templates now compile with the same compiler as embedded ones, so a
  recovered record renders exactly as it would have with its own template. The old WEVT compiler wrote empty elements
  as `/>` and kept empty optional attributes.
- **API:** the three `WriteTo` overloads take `WevtCache? wevtCache = null` as their **last** parameter, after the
  cancellation token, so existing calls still compile. Compiled callers need a rebuild (the method signatures changed).
- Template compilation now goes through one format-neutral compiler (phases 1–3 of the renderer refactor). It removes
  the separate XML, JSON and WEVT template compilers (~880 lines). Output is unchanged.
- **API:** `Parse`, `ParseAsync` and the `WriteTo` overloads take `bool salvage = false` as their last parameter.

---

### v0.2.1 — Checksum and robustness fixes, faster multi-threaded `WriteTo`

**Fixes:**

- **`validateChecksums: true` returned no records.** The chunk header check combined two separate CRC32s with XOR
  instead of computing one CRC32 over bytes 0–120 followed by 128–512, so every chunk failed and was skipped. Chunks
  flagged `NoCrc32` are now also treated as valid. Checksum-validated record counts now match the Rust `evtx` crate's
  `--validate-checksums` exactly on all 26 test samples.
- **`ParseAsync` with `validateChecksums: true`** sent checksum-failed chunks to record recovery, so it could return
  records `Parse` didn't. It now skips them, as `Parse` does and documents.
- **A corrupt chunk header could fail the whole file.** A header whose last record ID was below its first made `Parse`,
  `WriteTo` and `ParseAsync` throw (`ArgumentOutOfRangeException` inside an `AggregateException`). The ID range is now
  ignored when implausible; records are still found by walking the chunk.
- **A record claiming a huge number of substitutions** allocated memory sized from that count before rejecting it
  (about 430 MB for one bad record). The count is now checked against the record's remaining bytes first; only that
  record fails, with a diagnostic.
- **Two more ways a corrupt file could throw**, found by the new seeded corruption test. A record whose declared size
  was 2 GB or more wrapped the record-scan offset negative. A template pointer or size near `uint.MaxValue` slipped past
  the bounds check. Both now just end or skip the bad region.

**Performance:**

- **Multi-threaded `WriteTo` is pipelined.** It used to render a batch of chunks, then write the batch while every
  worker sat idle. Now workers render ahead into a bounded ring of buffers while the calling thread writes chunks in
  order. Output is unchanged.
- The benchmark CLI reads its input in parallel and is built with invariant globalization and speed-optimised AOT
  codegen.
- Together, on the 30 MB benchmark file (Apple M4, 8 threads), XML went from 15.5 ms to 12.9 ms and JSON from 17.0 ms
  to 14.1 ms (−17%). Single-threaded JSON went from 39.1 ms to 37.4 ms.

**Testing:**

- Golden output hashes for every sample, at 1 and 8 threads, now lock the exact output. The published AOT CLI is
  checked against them before benchmarking.
- `WriteTo` and `ParseAsync` are checked against `Parse` for every sample, format and thread count, with checksum
  validation off and on.
- A seeded corruption test (100 mutations per sample in CI) asserts that malformed input only ever throws
  `EvtxParseException`.

**Documentation:**

- `WevtCache` has never affected output: its templates are extracted but never consulted during parsing. The docs
  previously said it pre-compiled templates. They now say what it does today. A fallback for damaged templates (as in
  the Rust `evtx` crate) is planned for 0.3.0.
- The file header's checksum is read but not validated. The 0.1.0 notes below said otherwise.

No changes to output with default options. Golden output hashes for every sample are unchanged.

---

### v0.2.0 — Faster UTF-8 rendering

Rendering now goes straight from BinXml to UTF-8, with no UTF-16 strings in between. On the 30 MB benchmark file
(Apple M4, single thread) XML output went from 68.9 ms to 36.1 ms and JSON from 195.2 ms to 40.0 ms. With 8 threads,
XML went from 58.1 ms to 16.4 ms and JSON from 86.7 ms to 18.6 ms.

**New:**

- `EvtxParser.WriteTo(fileData, stream, ...)` — renders every record and writes the UTF-8 output straight to a
  `Stream`, using reusable per-worker buffers. Memory stays flat regardless of file size. Output is byte-identical
  to concatenating `Parse` output.
- `EvtxChunk.ParsedUtf8` — each record's rendered UTF-8 bytes (XML or JSON) as slices of the chunk's output buffer.
- `EvtxParser.ParseAsync(...)` — returns `IAsyncEnumerable<EvtxEvent>`. Chunks are parsed in parallel and events are
  yielded in file order as each chunk finishes, so consumers can start before the whole file is parsed.

**Breaking changes:**

- `EvtxChunk.ParsedJson` is now `IReadOnlyList<ReadOnlyMemory<byte>>?` (was `IReadOnlyList<byte[]>?`). Use
  `.Span` where you previously passed the array, e.g. `Encoding.UTF8.GetString(chunk.ParsedJson[i].Span)`.
  `EvtxEvent.Json` was already `ReadOnlyMemory<byte>` and is unchanged.

**Changes:**

- `EvtxChunk.ParsedXml` decodes each record from UTF-8 on first access and caches it. If you don't read it, it
  costs nothing.
- Numbers are formatted with the invariant culture. Previously, output could vary with the machine's locale.
- JSON escaping is vectorised. Embedded BinXml in JSON is written pre-escaped instead of rendered and then escaped
  (about 2× faster on those records).
- FILETIME, GUID, and hex formatting are span-based, and template parts are pre-encoded as UTF-8.

---

### v0.1.0 — Initial Release

High-performance .NET library for parsing Windows Event Log (.evtx) files.

**Features:**

- Full EVTX parsing — file headers, 64KB chunks, and individual event records
- BinXml decompilation — converts binary XML token streams to standard XML or UTF-8 JSON
- Template pre-compilation — compiles BinXml templates once for zero overhead on repeated renders
- WEVT template extraction — extracts and caches templates from Windows PE binaries (DLLs/EXEs). *(Not consulted
  during parsing until v0.3.0, where it became a fallback for damaged templates.)*
- Parallel chunk processing — configurable thread count for multi-core throughput
- CRC32 validation — optional checksum verification on chunk headers and chunk data *(broken until v0.2.1)*
- Resilient parsing — gracefully handles corrupted records, bad checksums, and malformed chunks

**Target Framework:** .NET 10.0

**Dependencies:**

- [PeNet](https://github.com/secana/PeNet) 6.0.0 — PE binary resource extraction for WEVT templates

---

## Installation

```
dotnet add package AxoParse.Evtx
```

Or add directly to your `.csproj`:

```xml
<PackageReference Include="AxoParse.Evtx" Version="0.2.1" />
```

---

## Quick Start

### Parse an EVTX file (XML output)

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

### JSON output

```csharp
EvtxParser parser = EvtxParser.Parse(fileData, format: OutputFormat.Json);

foreach (EvtxEvent evt in parser.GetEvents())
{
    string json = System.Text.Encoding.UTF8.GetString(evt.Json.Span);
    Console.WriteLine(json);
}
```

### Stream output to a file

```csharp
using FileStream output = File.Create("security.json");
int written = EvtxParser.WriteTo(fileData, output, OutputFormat.Json);
```

### Consume events as they're parsed

```csharp
await foreach (EvtxEvent evt in EvtxParser.ParseAsync(fileData, cancellationToken: token))
{
    if (evt.IsSuccess)
        Console.WriteLine(evt.Xml);
}
```

### Parallel parsing with checksum validation

```csharp
EvtxParser parser = EvtxParser.Parse(
    fileData,
    maxThreads: 4,
    validateChecksums: true);

Console.WriteLine($"Parsed {parser.TotalRecords} records from {parser.Chunks.Count} chunks");

foreach (string diag in parser.Diagnostics)
    Console.WriteLine($"Warning: {diag}");
```

### WEVT template cache

`WevtCache` is a **fallback** for damaged logs, as in the Rust `evtx` crate. When a record's embedded template is
missing (size 0), runs past its chunk, or won't compile, the record is rendered with the provider template of the
same GUID instead. A usable embedded template is never replaced, so passing a cache doesn't change output for an
intact log.

```csharp
using AxoParse.Evtx.Wevt;

WevtCache cache = new WevtCache();
cache.AddFromDirectory(@"C:\Windows\System32", "*.dll");

EvtxParser parser = EvtxParser.Parse(fileData, wevtCache: cache);
```

### Direct chunk access (advanced)

For performance-critical paths, access chunks directly instead of using `GetEvents()`:

```csharp
foreach (EvtxChunk chunk in parser.Chunks)
{
    for (int i = 0; i < chunk.Records.Count; i++)
    {
        EvtxRecord record = chunk.Records[i];
        string xml = chunk.ParsedXml[i];
        Console.WriteLine($"Record {record.EventRecordId}: {xml}");
    }
}
```

To write or hash output without decoding to `string`, use `chunk.ParsedUtf8[i].Span` instead.

---

## API Reference

### `EvtxParser`

**Namespace:** `AxoParse.Evtx.Evtx`

Primary entry point. Parses an entire EVTX file and provides access to chunks, records, and rendered events.

#### `Parse` (static method)

```csharp
public static EvtxParser Parse(
    byte[] fileData,
    int maxThreads = 0,
    OutputFormat format = OutputFormat.Xml,
    bool validateChecksums = false,
    WevtCache? wevtCache = null,
    CancellationToken cancellationToken = default)
```

| Parameter           | Default   | Description                                                                   |
|---------------------|-----------|-------------------------------------------------------------------------------|
| `fileData`          | —         | Complete EVTX file as byte array                                              |
| `maxThreads`        | `0`       | Thread count. `0` or `-1` = all cores, `1` = single-threaded, `N` = N threads |
| `format`            | `Xml`     | Output format. Chosen at parse time — compiled templates are format-specific  |
| `validateChecksums` | `false`   | When `true`, chunks failing CRC32 validation are skipped                      |
| `wevtCache`         | `null`    | Provider templates used when a record's embedded template is missing or corrupt |
| `cancellationToken` | `default` | Cancellation token (throws `OperationCanceledException` if cancelled)         |

**Returns:** Fully parsed `EvtxParser` instance.

**Throws:** `EvtxParseException` if data is too short or missing the EVTX magic signature.

#### `WriteTo` (static method)

```csharp
public static int WriteTo(
    byte[] fileData,
    Stream output,
    OutputFormat format = OutputFormat.Xml,
    int maxThreads = 0,
    bool validateChecksums = false,
    CancellationToken cancellationToken = default)

public static int WriteTo(string path, Stream output, /* same options */)
public static int WriteTo(SafeFileHandle file, Stream output, /* same options */)
```

The path and handle overloads stream the file: each worker reads the 64 KB chunk it renders with a positional read,
so the file is never loaded whole, files over 2 GB work, and the handle's position isn't used or changed. The path
overload opens the file with `FileShare.ReadWrite`, so a log another process still has open can be read. All three
produce identical output.

Parses the file and writes every record's rendered UTF-8 output to `output`. The order and bytes match concatenating
`Parse` output: valid chunks in file order, then records recovered from regions whose chunk header is damaged.
Workers render chunks into a bounded ring of reusable buffers while the calling thread writes them in order, so no
per-record output is retained and rendering overlaps with writing.
The stream is written to but not flushed or closed.

Per-record render diagnostics aren't reported. A record that fails to render writes nothing. The optional `wevtCache`
(the last parameter) works as in `Parse`.

**Returns:** Number of records written, counted the same way as `TotalRecords` for the same input.

**Throws:** `EvtxParseException` under the same conditions as `Parse`.

#### `ParseAsync` (static method)

```csharp
public static IAsyncEnumerable<EvtxEvent> ParseAsync(
    byte[] fileData,
    int maxThreads = 0,
    OutputFormat format = OutputFormat.Xml,
    bool validateChecksums = false,
    WevtCache? wevtCache = null,
    CancellationToken cancellationToken = default)
```

Same parsing as `Parse`, but events are streamed. Chunks are parsed in parallel and yielded in file order through a
reorder buffer, so the first events are available before the whole file is parsed. Parameters are the same as `Parse`.

#### `GetEvents`

```csharp
public IEnumerable<EvtxEvent> GetEvents()
```

Enumerates all parsed events across all chunks in file order. Each event pairs record metadata with rendered output and
optional diagnostic info.

#### Properties

| Property       | Type                       | Description                                             |
|----------------|----------------------------|---------------------------------------------------------|
| `Chunks`       | `IReadOnlyList<EvtxChunk>` | All successfully parsed 64KB chunks, in file order      |
| `TotalRecords` | `int`                      | Total event records across all chunks                   |
| `FileHeader`   | `EvtxFileHeader`           | Parsed file header (version, chunk count, flags)        |
| `Diagnostics`  | `IReadOnlyList<string>`    | Parser-level diagnostic messages (e.g., skipped chunks) |
| `RawData`      | `ReadOnlyMemory<byte>`     | Complete file bytes (retained for lazy record access)   |

---

### `EvtxEvent`

**Namespace:** `AxoParse.Evtx.Evtx`

```csharp
public readonly record struct EvtxEvent(
    EvtxRecord Record,
    string Xml,
    ReadOnlyMemory<byte> Json,
    string? Diagnostic)
```

A single parsed event pairing record metadata with rendered output. Primary consumption type via `GetEvents()`.

| Field        | Type                   | Description                                                                    |
|--------------|------------------------|--------------------------------------------------------------------------------|
| `Record`     | `EvtxRecord`           | Raw record metadata (ID, timestamps, offsets)                                  |
| `Xml`        | `string`               | Rendered XML string. Populated when `OutputFormat.Xml`, otherwise empty        |
| `Json`       | `ReadOnlyMemory<byte>` | Rendered UTF-8 JSON bytes. Populated when `OutputFormat.Json`, otherwise empty |
| `Diagnostic` | `string?`              | Error message if BinXml rendering failed; `null` on success                    |

| Property    | Type   | Description                                                        |
|-------------|--------|--------------------------------------------------------------------|
| `IsSuccess` | `bool` | `true` if the event rendered without errors (`Diagnostic is null`) |

---

### `EvtxRecord`

**Namespace:** `AxoParse.Evtx.Evtx`

```csharp
public readonly record struct EvtxRecord(
    uint Size,
    ulong EventRecordId,
    ulong WrittenTime,
    int EventDataFileOffset,
    int EventDataLength,
    uint SizeCopy)
```

Parsed representation of a single EVTX event record.

| Field                 | Type    | Description                                                     |
|-----------------------|---------|-----------------------------------------------------------------|
| `Size`                | `uint`  | Total record size in bytes (header + data + trailing size)      |
| `EventRecordId`       | `ulong` | Monotonically increasing event record identifier                |
| `WrittenTime`         | `ulong` | FILETIME timestamp when record was written                      |
| `EventDataFileOffset` | `int`   | Absolute byte offset of BinXml data within file buffer          |
| `EventDataLength`     | `int`   | Length in bytes of BinXml payload                               |
| `SizeCopy`            | `uint`  | Trailing copy of `Size` for backward traversal integrity checks |

| Property         | Type       | Description                                       |
|------------------|------------|---------------------------------------------------|
| `WrittenTimeUtc` | `DateTime` | Converts FILETIME `WrittenTime` to UTC `DateTime` |

#### Methods

```csharp
public static EvtxRecord? ParseEvtxRecord(ReadOnlySpan<byte> data, int fileOffset)
```

Parses a record from a span. Returns `null` if the record has invalid size (corrupted/zero data).

```csharp
public ReadOnlySpan<byte> GetEventData(byte[] fileData)
```

Returns event data as a span into the original file buffer.

---

### `EvtxChunk`

**Namespace:** `AxoParse.Evtx.Evtx`

Each EVTX file body consists of sequentially laid-out chunks, each exactly 64 KB. Self-contained units with own header,
event records in BinXml, and trailing unused space.

#### Constants

| Constant    | Value   | Description                         |
|-------------|---------|-------------------------------------|
| `ChunkSize` | `65536` | Size of each chunk in bytes (64 KB) |

#### Properties

| Property            | Type                                                  | Description                                                 |
|---------------------|-------------------------------------------------------|-------------------------------------------------------------|
| `Header`            | `EvtxChunkHeader`                                     | Parsed chunk header with record ranges, offsets, checksums  |
| `Records`           | `IReadOnlyList<EvtxRecord>`                           | Event records parsed from chunk's data area                 |
| `ParsedUtf8`        | `IReadOnlyList<ReadOnlyMemory<byte>>`                 | Rendered UTF-8 output (XML or JSON) per record; slices of one chunk buffer |
| `ParsedXml`         | `IReadOnlyList<string>`                               | Rendered XML strings, decoded lazily from `ParsedUtf8` (empty when format is JSON) |
| `ParsedJson`        | `IReadOnlyList<ReadOnlyMemory<byte>>?`                | Rendered UTF-8 JSON per record (`null` when format is XML)  |
| `RenderDiagnostics` | `IReadOnlyDictionary<int, string>`                    | Error messages for failed renders, keyed by record index    |
| `Templates`         | `IReadOnlyDictionary<uint, BinXmlTemplateDefinition>` | Template definitions from chunk's template pointer table    |

#### Methods

```csharp
public static EvtxChunk Parse(ReadOnlySpan<byte> chunkData, int chunkFileOffset)
```

Parses a 64KB chunk without BinXml rendering (header + templates + records only).

```csharp
public static EvtxChunk ParseStandalone(byte[] chunkData)
```

Parses a standalone 64KB chunk byte array with BinXml rendering. Treats the array as a self-contained "file" with
chunk-relative offsets. Designed for WASM interop.

```csharp
public EvtxEvent GetEvent(int index)
```

Returns the `EvtxEvent` at a specific index within the chunk.

---

### `EvtxChunkHeader`

**Namespace:** `AxoParse.Evtx.Evtx`

```csharp
public readonly record struct EvtxChunkHeader(...)
```

Chunk header (512 bytes). Tracks which event records are contained, provides fast-lookup caches, and stores CRC32
checksums.

| Field                       | Offset | Size    | Description                                                |
|-----------------------------|--------|---------|------------------------------------------------------------|
| `FirstEventRecordNumber`    | 8      | 8 bytes | First event record number in chunk                         |
| `LastEventRecordNumber`     | 16     | 8 bytes | Last event record number in chunk                          |
| `FirstEventRecordId`        | 24     | 8 bytes | First event record identifier in chunk                     |
| `LastEventRecordId`         | 32     | 8 bytes | Last event record identifier in chunk                      |
| `HeaderSize`                | 40     | 4 bytes | Always 128                                                 |
| `LastEventRecordDataOffset` | 44     | 4 bytes | Chunk-relative offset of last record's data                |
| `FreeSpaceOffset`           | 48     | 4 bytes | Chunk-relative offset where free space begins              |
| `EventRecordsChecksum`      | 52     | 4 bytes | CRC32 over event records data (bytes 512..FreeSpaceOffset) |
| `Flags`                     | 120    | 4 bytes | `ChunkFlags` status flags                                  |
| `Checksum`                  | 124    | 4 bytes | CRC32 of header bytes                                      |

#### Methods

```csharp
public static EvtxChunkHeader ParseEvtxChunkHeader(ReadOnlySpan<byte> data)
```

Parses 512-byte chunk header. Verifies `ElfChnk\0` signature.

```csharp
public bool ValidateDataChecksum(ReadOnlySpan<byte> chunkData)
```

Validates event records data checksum: `CRC32(bytes[512..FreeSpaceOffset])`.

```csharp
public bool ValidateHeaderChecksum(ReadOnlySpan<byte> chunkData)
```

Validates chunk header checksum: `CRC32(bytes[0..120]) XOR CRC32(bytes[128..512])`.

---

### `EvtxFileHeader`

**Namespace:** `AxoParse.Evtx.Evtx`

```csharp
public readonly record struct EvtxFileHeader(...)
```

File header occupying the first 4,096 bytes. Identifies the file as EVTX, tracks chunk/record ranges, and stores a CRC32
checksum.

| Field                  | Offset | Size    | Description                          |
|------------------------|--------|---------|--------------------------------------|
| `FirstChunkNumber`     | 8      | 8 bytes | Number of oldest chunk in file       |
| `LastChunkNumber`      | 16     | 8 bytes | Number of most recent chunk in file  |
| `NextRecordIdentifier` | 24     | 8 bytes | Next event record ID to be assigned  |
| `HeaderSize`           | 32     | 4 bytes | Always 128                           |
| `MinorFormatVersion`   | 36     | 2 bytes | Minor version (e.g., 1 for v3.1)     |
| `MajorFormatVersion`   | 38     | 2 bytes | Major version (e.g., 3 for v3.1)     |
| `HeaderBlockSize`      | 40     | 2 bytes | Always 4096 (chunk data starts here) |
| `NumberOfChunks`       | 42     | 2 bytes | Number of chunks in file             |
| `FileFlags`            | 120    | 4 bytes | `HeaderFlags` (Dirty/Full/NoCrc32)   |
| `Checksum`             | 124    | 4 bytes | CRC32 of first 120 bytes             |

#### Methods

```csharp
public static EvtxFileHeader ParseEvtxFileHeader(ReadOnlySpan<byte> data)
```

Parses first 128 bytes of raw EVTX file. Validates `ElfFile\0` magic signature at offset 0.

**Throws:** `EvtxParseException` with `FileHeaderTooShort` or `InvalidFileSignature`.

---

### `OutputFormat`

**Namespace:** `AxoParse.Evtx.Evtx`

```csharp
public enum OutputFormat
{
    Xml,   // Render each event as an XML string
    Json   // Render each event as a UTF-8 JSON byte array
}
```

Format is chosen at parse time because compiled template caches store precompiled format-specific fragments.

---

### `HeaderFlags`

**Namespace:** `AxoParse.Evtx.Evtx`

```csharp
[Flags]
public enum HeaderFlags : uint
{
    None    = 0x0,  // File is clean and not full
    Dirty   = 0x1,  // Log was not closed cleanly (dirty shutdown/crash)
    Full    = 0x2,  // Log has reached configured maximum size
    NoCrc32 = 0x4   // Header CRC32 should not be validated
}
```

Stored at offset 120 of the EVTX file header.

---

### `ChunkFlags`

**Namespace:** `AxoParse.Evtx.Evtx`

```csharp
[Flags]
public enum ChunkFlags : uint
{
    None    = 0x0,  // Chunk in normal state
    Dirty   = 0x1,  // Chunk has been modified since last flush
    NoCrc32 = 0x4   // CRC32 checksums not present or should not be validated
}
```

Stored at chunk header offset 120.

---

### `EvtxParseException`

**Namespace:** `AxoParse.Evtx.Evtx`

```csharp
public class EvtxParseException : Exception
{
    public EvtxParseError ErrorCode { get; }
}
```

Thrown when EVTX data fails structural validation. Use `ErrorCode` for programmatic handling:

```csharp
try
{
    EvtxParser parser = EvtxParser.Parse(fileData);
}
catch (EvtxParseException ex) when (ex.ErrorCode == EvtxParseError.InvalidFileSignature)
{
    Console.WriteLine("Not an EVTX file");
}
```

### `EvtxParseError`

**Namespace:** `AxoParse.Evtx.Evtx`

| Value                   | Thrown By                              | Description                       |
|-------------------------|----------------------------------------|-----------------------------------|
| `FileHeaderTooShort`    | `EvtxFileHeader.ParseEvtxFileHeader`   | Data shorter than 128 bytes       |
| `InvalidFileSignature`  | `EvtxFileHeader.ParseEvtxFileHeader`   | Missing `ElfFile\0` magic         |
| `ChunkHeaderTooShort`   | `EvtxChunkHeader.ParseEvtxChunkHeader` | Chunk data shorter than 512 bytes |
| `InvalidChunkSignature` | `EvtxChunkHeader.ParseEvtxChunkHeader` | Missing `ElfChnk\0` magic         |

---

### `WevtCache`

**Namespace:** `AxoParse.Evtx.Wevt`

Templates extracted and compiled from WEVT_TEMPLATE resources in Windows provider PE binaries, keyed by GUID.

`WevtCache` is a **fallback** for damaged logs, as in the Rust `evtx` crate. When a record's embedded template is
missing (size 0), runs past its chunk, or won't compile, the record is rendered with the provider template of the
same GUID instead. A usable embedded template is never replaced, so passing a cache doesn't change output for an
intact log.

#### Properties

| Property        | Type  | Description                                                   |
|-----------------|-------|---------------------------------------------------------------|
| `Count`         | `int` | Total template GUIDs in cache (including failed compilations) |
| `CompiledCount` | `int` | Templates successfully compiled (O(n); diagnostics use only)  |

#### Methods

```csharp
public int AddFromFile(string filePath)
```

Reads a PE file from disk and adds WEVT templates to cache. Returns number of new templates added.

```csharp
public int AddFromDirectory(string directory, string pattern = "*.dll")
```

Scans a directory for PE files matching a glob pattern. Returns total new templates added across all files.

```csharp
public int AddFromPeData(byte[] peData)
```

Extracts WEVT templates from raw PE bytes, compiles them, and adds to cache. Returns number of new templates added.

**Duplicate handling:** First-in wins. If a template GUID already exists in the cache, subsequent additions with the
same GUID are silently ignored.

---

## Error Handling

The library follows a clear philosophy: **throw on "this isn't an EVTX file" errors, recover silently from "this EVTX
file has corruption" errors.**

### What throws

- File data shorter than 128 bytes (`FileHeaderTooShort`)
- Missing `ElfFile\0` magic signature (`InvalidFileSignature`)
- Chunk data shorter than 512 bytes (`ChunkHeaderTooShort`)
- Missing `ElfChnk\0` chunk magic (`InvalidChunkSignature`)

### What recovers silently

- **Chunks with bad magic** — skipped, then the 64KB region is scanned for individual records by `0x2A2A` record magic
- **Records that fail BinXml parsing** — produce empty output (`""` for XML, a zero-length slice for JSON) with a
  diagnostic message on the `EvtxEvent` (`WriteTo` writes nothing for them)
- **CRC32 checksum failures** — only enforced when `validateChecksums: true`; failing chunks are skipped entirely

Check `parser.Diagnostics` for parser-level warnings and `evt.IsSuccess` / `evt.Diagnostic` for per-record status.
