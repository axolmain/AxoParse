# AxoParse.Evtx — v0.1.0 Release Notes & API Reference

## Release Notes

### v0.1.0 — Initial Release

High-performance .NET library for parsing Windows Event Log (.evtx) files.

**Features:**

- Full EVTX parsing — file headers, 64KB chunks, and individual event records
- BinXml decompilation — converts binary XML token streams to standard XML or UTF-8 JSON
- Template pre-compilation — compiles BinXml templates once for zero overhead on repeated renders
- WEVT template cache — extracts and caches templates from Windows PE binaries (DLLs/EXEs) for offline parsing
- Parallel chunk processing — configurable thread count for multi-core throughput
- CRC32 validation — optional checksum verification on file headers, chunk headers, and chunk data
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
<PackageReference Include="AxoParse.Evtx" Version="0.1.0" />
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

Pre-load templates from Windows provider PE binaries for richer event data:

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
| `wevtCache`         | `null`    | Pre-built template cache from provider PE binaries                            |
| `cancellationToken` | `default` | Cancellation token (throws `OperationCanceledException` if cancelled)         |

**Returns:** Fully parsed `EvtxParser` instance.

**Throws:** `EvtxParseException` if data is too short or missing the EVTX magic signature.

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
| `ParsedXml`         | `IReadOnlyList<string>`                               | Rendered XML strings (empty when format is JSON)            |
| `ParsedJson`        | `IReadOnlyList<byte[]>?`                              | Rendered UTF-8 JSON byte arrays (`null` when format is XML) |
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

Offline template cache built from WEVT_TEMPLATE resources in Windows provider PE binaries. Pre-compiles templates for
injection into the parser's cache before chunk processing — zero hot-path overhead.

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
- **Records that fail BinXml parsing** — produce empty output (`""` for XML, `[]` for JSON) with a diagnostic message on
  the `EvtxEvent`
- **CRC32 checksum failures** — only enforced when `validateChecksums: true`; failing chunks are skipped entirely

Check `parser.Diagnostics` for parser-level warnings and `evt.IsSuccess` / `evt.Diagnostic` for per-record status.
