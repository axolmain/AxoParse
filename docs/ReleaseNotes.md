# AxoParse.Evtx — Release Notes & API Reference

## Release Notes

### v0.3.0 (unreleased) — New API, streaming input, strict rendering with opt-in salvage, WEVT fallback, faster rendering

**This release breaks the public API** (see API below). Rendered record output for intact logs is unchanged, except
for one bug fix: entity references with inline names (28 records across three samples now render in full; see Fixes).
Every other rendering change affects corrupt input only. `WriteTo` now writes a line feed after each record.

**API (breaking):**

- **One namespace, `AxoParse.Evtx`** (was `AxoParse.Evtx.Evtx` and `AxoParse.Evtx.Wevt`).
- **`EvtxOptions`** (an immutable record) replaces the optional parameters: `Format`, `MaxThreads`,
  `ValidateChecksums`, `Salvage`, `WevtCache`, `XmlEventsRoot`. Every entry point is
  `(input, [output,] EvtxOptions? options = null, CancellationToken ct = default)`.
- **`EvtxParser` is a static class.** `Parse(byte[] | path)` returns an **`EvtxLog`** (`Header`, `Chunks`,
  `RecordCount`, `Events`, `Diagnostics`) instead of an `EvtxParser` instance (`FileHeader`, `TotalRecords`,
  `GetEvents()` and `RawData` are gone). `Parse(path)` reads chunks as it parses instead of loading the file.
- **`ReadEvents` / `ReadEventsAsync`** (`byte[]` or path) replace `ParseAsync`. They stream events with bounded memory
  (a few chunks per worker) instead of buffering every parsed chunk.
- **`WriteTo(byte[] | path | Stream input, Stream output, ...)`**: the `SafeFileHandle` overload is replaced by a
  seekable-`Stream` overload (a `FileStream` keeps the positional-read fast path). Output is now framed: a line feed
  after each record, so JSON output is JSON Lines; `XmlEventsRoot` wraps XML output in `<Events>`. Record bytes are
  unchanged.
- **`EvtxEvent`** is a 16-byte view: `RecordId`, `WrittenTimeUtc`, `FileOffset`, `Utf8`, `GetText()`, `Diagnostic`,
  `IsSuccess`, `IsSalvaged`. `Record`, `Xml` and `Json` are gone; `Utf8` holds the output in either format.
- **`EvtxChunk`** exposes `Header`, `FileOffset`, `Recovered` and `Events`. `Records`, `ParsedXml`, `ParsedJson`,
  `ParsedUtf8`, `RenderDiagnostics`, `Templates`, `GetEvent` and the public `Parse` overloads are gone;
  `EvtxParser.ParseChunk(span)` replaces `ParseStandalone`, and `EvtxParser.ParseFileHeader(span)` replaces
  `EvtxFileHeader.ParseEvtxFileHeader`.
- **Typed diagnostics:** `EvtxDiagnostic` (`Kind`, `RecordId`, `FileOffset`, `Message`) replaces diagnostic strings
  (messages keep their text). `EvtxLog.Diagnostics` now lists record diagnostics as well as skipped chunks.
- **`OutputFormat.Json` is renamed `OutputFormat.JsonStructured`** (same bytes), and **`OutputFormat.Json` is now the
  new compact format** (see New). Code that asked for `Json` gets the compact shape; ask for `JsonStructured` to keep
  the old one.
- Internal now: `EvtxRecord`, `BinXmlTemplateDefinition`, `Crc32`, and the header `Parse*` / `Validate*Checksum`
  methods.

**New:**

- **Compact JSON** (`OutputFormat.Json`): `{"System":{"EventID":4624,…},"EventData":{"TargetUserName":"…",…}}` with
  attributes as plain keys, EventData keyed by `Name`, repeated elements as arrays, typed values (numbers, booleans,
  arrays) and embedded BinXml nested in place; documented in `docs/JsonFormat.md`. Every record of every sample matches
  its own XML under those rules. Templates are compiled into JSON plans, so a record is written straight from its
  values (templates whose shape varies more use a tree; both give identical bytes). About 1.5x the instructions of
  structured JSON on the 30 MB sample.
- **Typed fields** (opt-in with `EvtxOptions.Fields`): `EvtxEvent.EventId`, `Version`, `Level`, `Task`, `Opcode`,
  `Keywords`, `TimeCreated`, `Provider`, `ProviderGuid`, `Channel`, `Computer`, `ProcessId`, `ThreadId`, `UserId`,
  `ActivityId`, and `EventData` (EventData/UserData name/value pairs, `TryGetValue`). Read from each template's
  substitution values as records are parsed, not from rendered text; every field and pair of every sample record
  matches the record's own XML. `FieldsOnly = true` skips rendering (about 2.5x faster than rendering XML). Parses
  that don't ask for fields are unchanged in speed.
- **Filtering before rendering** (`EvtxOptions.Filter` / `EvtxFilter`): event IDs, providers, levels, a TimeCreated
  range and a record-ID range, applied to every entry point including `WriteTo`. Events that don't pass are never
  rendered, and record-ID ranges skip whole chunks by their headers (a dirty chunk's last ID isn't trusted: the samples
  include dirty chunks holding records past it). **`EvtxParser.FindEvent(path|byte[], recordId)`** looks one record up
  (about 0.6 ms on the 30 MB sample against 74 ms for a parse), and `FromRecordId` resumes after the last event read.
- **Streaming input.** `WriteTo`, `ReadEvents` and `Parse` given a path (or a stream) read each 64 KB chunk as a
  worker renders it (positional reads for files), so reading overlaps rendering and the file is never loaded whole.
  Output is identical to the in-memory overloads. The benchmark CLI uses it (see the peak memory column below).
- **`WevtCache` now works, as a fallback.** When a record's embedded template is missing (size 0), runs past its chunk,
  or is corrupt, the record is rendered with the provider template of the same GUID, in XML and JSON, as in the Rust
  `evtx` crate. A usable embedded template is never replaced (tested: same-GUID provider templates leave an intact
  log's output byte-identical). Provider templates compile with the same compiler as embedded ones, so a recovered
  record renders exactly as it would have with its own template.
- **`Salvage = true`** keeps the well-formed part of a record whose BinXml is
  corrupt instead of failing it. Decoding stops at the corruption and every open element is closed, so the XML or JSON
  is still well-formed. `EvtxEvent.Diagnostic` says where it stopped (`BinXml salvaged …`). Off by default.

**Performance:**

On the 30 MB sample (Apple M4, AOT benchmark CLI), v0.2.1 and v0.3.0 measured back to back (median of 20 runs):

| | v0.2.1 | v0.3.0 | peak memory |
|---|---|---|---|
| XML, 1 thread | 33.6 ms | 25.4 ms (−24%) | 39 → 8 MiB |
| JSON, 1 thread | 38.0 ms | 29.6 ms (−22%) | 39 → 9 MiB |
| XML, 8 threads | 12.9 ms | 10.7 ms (−17%) | 49 → 18 MiB |
| JSON, 8 threads | 14.2 ms | 12.0 ms (−16%) | 61 → 28 MiB |

The machine was busy during these runs; the README's cross-parser table is from a quiet run. Where it came from:

- Streaming input (above), and template-instance work done once per chunk instead of once per record: each template
  definition is resolved once and remembered by offset, instead of two dictionary lookups per record.
- Substitution values are read in one pass, with sizes and types used in place, instead of being copied into three
  stack arrays per record.
- Integers are formatted with `Utf8Formatter`; short ASCII strings with nothing to escape take a scalar fast path;
  FILETIME formatting reuses the last calendar date; `WriteTo` no longer preloads each chunk's template table.
- Each change was kept only if it reduced instruction counts. Tried and dropped: a hand-written digit writer (slower
  than `Utf8Formatter`), `SkipLocalsInit` on the per-record path (−0.3%, not worth enabling unsafe code), an inline copy
  for short appends (slower than `memmove`), and remembering the last SID.

**Fixes:**

- **Entity references with inline names truncated the event.** An entity reference's name (like `quot` in
  `&quot;`) uses the same name structure as an element name, so it can be defined inline. Its inline bytes were
  read as tokens, and everything after the first such entity was lost. 26 records in the two CAPI2 samples and 2 in
  `Archive-ForwardedEvents-test.evtx` were cut short this way (for example, an HTTP header list ended at
  `Via: 1.1 [&quot;`). All records of those three samples now match the Rust `evtx` crate's output.
- **Corrupt records could render invalid JSON while reporting success.** Found by the corruption test, which now
  requires every non-empty JSON record to parse:
  - A record or template whose bytes held more than one top-level item rendered `{…}{…}` or `{…},…`. A record and a
    template body now each end after their root element.
  - A nested template that rendered nothing left a dangling comma in its parent's `#content`; it now renders `""`.
  - A SYSTEMTIME field out of range for its digits (month 300, say) was written as non-digit bytes, including control
    characters. Such fields are now written in full.
- **A template body that ran past its own end failed every record using it.** The compiler's read past the end threw
  instead of reporting the template as corrupt, so records couldn't fall back to a provider template and the compile
  was retried for each one. It now counts as a corrupt template like any other.
- JSON for templates that don't compile now matches the compiled path: attribute values are formatted as JSON (they
  were XML-escaped, so `&` came out as `&amp;`), and an empty value in element content renders as `""`.

**Behavior changes (corrupt input only):**

- **Corrupt BinXml fails the record instead of rendering junk.** Records, embedded BinXml values and templates that
  aren't compiled are decoded by one strict decoder shared by XML and JSON, as in the Rust `evtx` crate. An unknown
  token, nesting deeper than 64 levels, a name running past the data, a substitution outside a template, or an
  element inside an attribute value fails the record with a diagnostic. Before, the bytes were skipped and the record
  could render partial or meaningless output (like `<></>`) while reporting success. Use `Salvage = true` to keep the
  well-formed part instead.
- **Rendering only reads a record's own chunk.** A template body or substitution value that ran past the end of its
  64 KB chunk used to be read from the next chunk's bytes. Now the template is treated as unavailable or the record
  fails (with a diagnostic), as in the Rust `evtx` crate, which parses each chunk from its own buffer.
- An optional attribute whose value index is past the instance's values is omitted (the compiled and decoded paths
  used to disagree).

**Internals:**

- The renderer was rebuilt around one format-neutral template compiler and one strict decoder that drives a writer
  per format (`XmlWriter`, `JsonWriter`). Compiled templates are built by replaying a template through the same
  writer, so compiled and decoded output follow one set of rules; a new test checks they match byte for byte on every
  sample and seeded mutations. One compiled template type, one executor, one template-instance handler and one value
  formatter replace the per-format pairs, and the separate XML, JSON and WEVT template compilers and both hand-written
  interpreters are gone.
- `Parse`, `ReadEvents`, `ReadEventsAsync` and `WriteTo` run on one ordered chunk pipeline with one two-pass chunk
  classification, so they agree on order and output by construction. Workers check chunk magic and checksums, so
  `ValidateChecksums` costs less wall time. `Parse` no longer preloads each chunk's template table.

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
<PackageReference Include="AxoParse.Evtx" Version="0.3.0" />
```

---

## Quick Start

```csharp
using AxoParse.Evtx;

// Whole log
EvtxLog log = EvtxParser.Parse("security.evtx");
foreach (EvtxEvent evt in log.Events)
    Console.WriteLine(evt.IsSuccess ? evt.GetText() : $"Record {evt.RecordId} failed: {evt.Diagnostic?.Message}");

// Streaming, bounded memory
foreach (EvtxEvent evt in EvtxParser.ReadEvents("security.evtx", new EvtxOptions { MaxThreads = 4 }))
    Console.WriteLine(evt.RecordId);

// Export as JSON Lines
using FileStream output = File.Create("security.jsonl");
EvtxParser.WriteTo("security.evtx", output, new EvtxOptions { Format = OutputFormat.JsonStructured });
```

See [Getting Started](GettingStarted.md) for more.

---

## API Reference

Every public type is in the `AxoParse.Evtx` namespace. Every type is immutable and safe to use from any thread, except
`WevtCache` (add templates before sharing it).

### `EvtxParser`

```csharp
public static class EvtxParser
```

Static entry points. All of them take an optional `EvtxOptions` (null = `EvtxOptions.Default`) and a
`CancellationToken`, and produce events in one order whatever the thread count: valid chunks in file order, then
records recovered from regions whose chunk header is damaged.

| Method | Returns | Description |
|--------|---------|-------------|
| `Parse(byte[] fileData, EvtxOptions? options = null, CancellationToken ct = default)` | `EvtxLog` | Parses a file held in memory. |
| `Parse(string path, ...)` | `EvtxLog` | Parses a file from disk, reading chunks as it goes (only the output is held in memory; files over 2 GB work). |
| `ReadEvents(byte[] fileData \| string path, ...)` | `IEnumerable<EvtxEvent>` | Streams events while later chunks are parsed in the background; a few chunks per worker in memory. The path overload opens the file when enumeration starts and closes it when it ends. |
| `ReadEventsAsync(byte[] fileData \| string path, ...)` | `IAsyncEnumerable<EvtxEvent>` | As `ReadEvents`, waiting for chunks without blocking a thread. |
| `WriteTo(byte[] fileData \| string path \| Stream input, Stream output, ...)` | `int` (records written) | Writes every event's UTF-8 output, each followed by a line feed (JSON Lines for JSON). `input` must be readable and seekable; a `FileStream` is read with positional reads. `output` is written to but not flushed or closed. |
| `FindEvent(byte[] fileData \| string path, ulong recordId, ...)` | `EvtxEvent?` | The first event with that record ID, or null. Skips chunks that can't hold it and renders only matching records. |
| `ParseChunk(ReadOnlySpan<byte> chunkData, EvtxOptions? options = null)` | `EvtxChunk` | Decodes one standalone 64 KB chunk (carving, chunk-by-chunk reading). Offsets are chunk-relative. |
| `ParseFileHeader(ReadOnlySpan<byte> data)` | `EvtxFileHeader` | Decodes the first 128 bytes of a file. |

Throws `EvtxParseException` for a missing or invalid file header (or, from `ParseChunk`, chunk signature), and
`OperationCanceledException` when cancelled.

---

### `EvtxOptions`

```csharp
public sealed record EvtxOptions
```

| Property            | Type           | Default | Description |
|---------------------|----------------|---------|-------------|
| `Format`            | `OutputFormat` | `Xml`   | Format events are rendered in |
| `MaxThreads`        | `int`          | `0`     | Worker threads; `0` or negative = all cores, `1` = calling thread only |
| `ValidateChecksums` | `bool`         | `false` | Skip chunks failing CRC32 validation (reported as `ChunkChecksumMismatch`) |
| `Salvage`           | `bool`         | `false` | Keep the well-formed prefix of a record whose BinXml is corrupt |
| `WevtCache`         | `WevtCache?`   | `null`  | Provider templates for records whose embedded template is missing or corrupt |
| `Filter`            | `EvtxFilter?`  | `null`  | Which events to produce (see `EvtxFilter`) |
| `Fields`            | `EvtxFields`   | `None`  | Typed fields to read: `System`, `EventData` (implies `System`) or `All` |
| `FieldsOnly`        | `bool`         | `false` | Read typed fields without rendering; `Utf8` stays empty (not for `WriteTo`) |
| `XmlEventsRoot`     | `bool`         | `false` | `WriteTo` with XML only: wrap the records in `<Events>` … `</Events>` lines |

`EvtxOptions.Default` holds the defaults. Derive variants with `with`.

---

### `EvtxFilter`

```csharp
public sealed record EvtxFilter
```

An event must pass every criterion that is set (null accepts everything). Events that don't pass are dropped before
rendering.

| Property       | Type                            | Description |
|----------------|---------------------------------|-------------|
| `EventIds`     | `IReadOnlyCollection<ushort>?`  | `System/EventID` values to keep |
| `Providers`    | `IReadOnlyCollection<string>?`  | `System/Provider@Name` values to keep, ignoring case |
| `Levels`       | `IReadOnlyCollection<byte>?`    | `System/Level` values to keep |
| `From` / `To`  | `DateTime?`                     | `TimeCreated` range in UTC: `From` inclusive, `To` exclusive |
| `FromRecordId` / `ToRecordId` | `ulong?`         | Record-ID range, both inclusive; skips whole chunks by header |

---

### `EvtxLog`

```csharp
public sealed class EvtxLog
```

| Property      | Type                              | Description |
|---------------|-----------------------------------|-------------|
| `Header`      | `EvtxFileHeader`                  | The file header |
| `Chunks`      | `IReadOnlyList<EvtxChunk>`        | Chunks that produced events, in output order (checksum-skipped chunks excluded) |
| `RecordCount` | `int`                             | Total events across `Chunks` |
| `Events`      | `IEnumerable<EvtxEvent>`          | Every event, chunk by chunk |
| `Diagnostics` | `IReadOnlyList<EvtxDiagnostic>`   | Skipped chunks and records that didn't decode cleanly, in output order |

---

### `EvtxChunk`

```csharp
public sealed class EvtxChunk
```

| Member       | Type                        | Description |
|--------------|-----------------------------|-------------|
| `Header`     | `EvtxChunkHeader`           | Parsed chunk header (all zero for a recovered region) |
| `FileOffset` | `long`                      | Absolute file offset of the chunk |
| `Recovered`  | `bool`                      | True for a region whose chunk header was damaged; its records were found by scanning |
| `Events`     | `IReadOnlyList<EvtxEvent>`  | The chunk's events, in record order |
| `ChunkSize`  | `const int`                 | 65536 |

---

### `EvtxEvent`

```csharp
public readonly struct EvtxEvent
```

A 16-byte view of one record of an `EvtxChunk`; reading it allocates nothing except `GetText()`.

| Member           | Type                     | Description |
|------------------|--------------------------|-------------|
| `RecordId`       | `ulong`                  | Event record ID from the record header |
| `WrittenTimeUtc` | `DateTime`               | Record header FILETIME as UTC (`DateTime.MinValue` if out of range) |
| `FileOffset`     | `long`                   | Absolute file offset of the record header |
| `Utf8`           | `ReadOnlyMemory<byte>`   | Rendered output; empty when rendering failed |
| `GetText()`      | `string`                 | `Utf8` decoded to a new string |
| `Diagnostic`     | `EvtxDiagnostic?`        | Why the record didn't decode cleanly, or null |
| `IsSuccess`      | `bool`                   | No diagnostic |
| `IsSalvaged`     | `bool`                   | `Utf8` holds only the part decoded before corruption |
| `EventId`, `Version`, `Level`, `Task`, `Opcode`, `Keywords` | integers | Typed `System` fields (`Fields` must include `System`) |
| `TimeCreated`    | `DateTime`               | `System/TimeCreated@SystemTime`, UTC (written time if absent) |
| `Provider`, `ProviderGuid`, `Channel`, `Computer`, `UserId`, `ActivityId`, `ProcessId`, `ThreadId` | | Other `System` fields; null or 0 when absent |
| `EventData`      | `EvtxEventData`          | EventData/UserData pairs (`Fields` must include `EventData`) |

Reading a typed field that wasn't requested throws `InvalidOperationException`.

`EvtxEventData` is an `IReadOnlyList<KeyValuePair<string, string>>` with `TryGetValue(name, out value)` and
`GetUtf8Value(index)`.

---

### `EvtxDiagnostic` and `EvtxDiagnosticKind`

```csharp
public readonly record struct EvtxDiagnostic(EvtxDiagnosticKind Kind, ulong? RecordId, long FileOffset, string Message);
```

| Kind                    | RecordId | FileOffset | Meaning |
|-------------------------|----------|------------|---------|
| `ChunkChecksumMismatch` | null     | chunk      | Chunk failed CRC32 validation and was skipped |
| `RecordRenderFailed`    | set      | record     | BinXml couldn't be decoded; output is empty |
| `RecordSalvaged`        | set      | record     | Output ends where decoding stopped (`Salvage`) |

---

### `OutputFormat`

```csharp
public enum OutputFormat
{
    Xml,            // The event's XML, as Event Viewer shows it
    JsonStructured, // Lossless JSON of the XML tree: {"#name", "#attrs", "#content"} per element
    Json            // Compact JSON with typed values (docs/JsonFormat.md)
}
```

---

### `EvtxChunkHeader`

**Namespace:** `AxoParse.Evtx`

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

Read it with `EvtxChunk.Header`.

---

### `EvtxFileHeader`

**Namespace:** `AxoParse.Evtx`

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

Read it with `EvtxLog.Header` or `EvtxParser.ParseFileHeader`.

---

### `HeaderFlags`

**Namespace:** `AxoParse.Evtx`

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

**Namespace:** `AxoParse.Evtx`

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

### `EvtxParseException` and `EvtxParseError`

```csharp
public sealed class EvtxParseException : Exception
{
    public EvtxParseError ErrorCode { get; }
}
```

| Value                   | Thrown by                                     | Description                 |
|-------------------------|-----------------------------------------------|-----------------------------|
| `FileHeaderTooShort`    | every file entry point, `ParseFileHeader`     | Data shorter than 128 bytes |
| `InvalidFileSignature`  | every file entry point, `ParseFileHeader`     | Missing `ElfFile\0` magic   |
| `ChunkHeaderTooShort`   | (reserved)                                    | Chunk data shorter than 512 bytes |
| `InvalidChunkSignature` | `ParseChunk`                                  | Missing `ElfChnk\0` magic   |

---

### `WevtCache`

**Namespace:** `AxoParse.Evtx`

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

The library follows a clear philosophy: **throw on "this isn't an EVTX file" errors, recover from "this EVTX file has
corruption" errors** and report them as `EvtxDiagnostic`s. See [Error Handling](ErrorHandling.md).
