# EVTX → XML Pipeline

```
EVTX File bytes
 → EvtxFileHeader (4096 bytes)
 → EvtxChunk[] (64KB each, parsed in parallel)
   → EvtxRecord[] (binary records within each chunk)
     → BinXmlParser (binary XML tokens → XML string)
       → EvtxEvent (record metadata + rendered XML)
```

---

## Stage 1 — File Header

`EvtxFileHeader.ParseEvtxFileHeader()` reads the first 128 bytes. It validates the `"ElfFile\0"` magic, reads
`HeaderBlockSize` (always 4096 — where chunks start), chunk count, and file flags. It's a `readonly record struct` — no
heap allocations.

## Stage 2 — Chunk Discovery & Parallel Parse (`EvtxParser.Parse`)

Runs in 4 phases:

1. **Sequential scan** (lines 87-114): walks 64KB-aligned regions after the header, checks for `"ElfChnk\0"` magic,
   optionally validates CRC32 checksums. Collects valid chunk offsets into an `int[]`.
2. **Parallel chunk parse** (lines 116-174): `Parallel.For` with thread-local `Dictionary<Guid, CompiledTemplate?>`
   caches (avoids lock contention). Each thread calls `EvtxChunk.Parse()`. After threads finish, local caches merge into
   a shared `ConcurrentDictionary` via `TryAdd`.
3. **Aggregate** (lines 176-184): collects chunks and sums record counts.
4. **Recovery** (lines 186-244): re-scans slots that didn't have valid magic and attempts `EvtxChunk.ParseHeaderless()`
   to salvage records from corrupted chunks.

## Stage 3 — Inside `EvtxChunk.Parse()`

Each 64KB chunk is self-contained:

- **Chunk header** (512 bytes): `EvtxChunkHeader` provides `FreeSpaceOffset` (scan boundary) and record ID ranges.
- **Template preload**: A 32-entry chained hash table at chunk offset 384 stores pointers to `BinXmlTemplateDefinition`
  structs. `PreloadFromChunk()` follows each chain, reading `{NextPtr, Guid, DataSize}` per template. Result:
  `Dictionary<uint, BinXmlTemplateDefinition>` keyed by chunk-relative offset.
- **Record walk**: `ReadRecords()` scans from byte 512 to `FreeSpaceOffset`, looking for the 4-byte `0x00002A2A` record
  magic. Each match calls `EvtxRecord.ParseEvtxRecord()`.

## Stage 4 — Record Parsing (`EvtxRecord`)

`MemoryMarshal.Read<RecordHeaderLayout>()` zero-copy reads the 24-byte header:

| Offset | Size | Field                    |
|--------|------|--------------------------|
| 0      | 4    | Signature (`0x00002A2A`) |
| 4      | 4    | Size (total bytes)       |
| 8      | 8    | EventRecordId            |
| 16     | 8    | WrittenTime (FILETIME)   |

The event data (raw BinXml) starts at offset 24 and spans `Size - 28` bytes (last 4 bytes are a trailing size copy for
integrity). `EvtxRecord` is a `readonly record struct` — stays on the stack.

## Stage 5 — BinXml Rendering (the core)

`BinXmlParser.ParseRecord()` takes an `EvtxRecord`, slices the event data from the file buffer, and writes XML into a
`ValueStringBuilder` (a `ref struct` backed by `stackalloc char[1024]`, growing via `ArrayPool` when needed).

**Token dispatch** (`ParseTopLevel`): loops over BinXml bytes, switching on the token:

- `0x0F` FragmentHeader → skip 4 bytes
- `0x01/0x41` OpenStartElement → `ParseElement()` (inline XML)
- `0x0C` TemplateInstance → `ParseTemplateInstance()` ← this is the hot path

**`ParseTemplateInstance()`** — nearly every Windows event uses this. A template is a pre-defined XML skeleton with
numbered substitution slots:

1. `ReadTemplateInstanceData()` parses the template reference:
    - Reads `defDataOffset` (chunk-relative pointer to template definition)
    - Reads `numValues` and `numValues × SubstitutionDescriptor` (each: `ushort Size`, `byte Type`)
    - Computes file offsets for each substitution value
2. Cache lookup by template GUID:
    - **Cache miss** → `CompileTemplate()` walks the template body tokens and produces a `CompiledTemplate`: parallel
      arrays `Parts[N+1]` (static XML fragments) and `Slots[N]` (substitution indices + metadata) in a zipper pattern.
    - **Cache hit** → `WriteCompiled()` — the fast path.
3. `WriteCompiled()` — the innermost hot loop:
   ```
   Parts[0] + value(Slots[0]) + Parts[1] + value(Slots[1]) + ... + Parts[N]
   ```
4. Each slot dispatches `WriteBinXmlValue()` for its typed value. No BinXml re-parsing occurs.

**`WriteBinXmlValue()` type dispatch** — formats each value type directly into the `ValueStringBuilder`:

- **String**: `MemoryMarshal.Cast<byte, char>` (UTF-16LE) + XML entity escaping
- **FileTime**: custom tick arithmetic → `yyyy-MM-ddTHH:mm:ss.fffffffZ` (no `DateTime` allocation)
- **GUID**: custom formatting with LE/BE byte reordering
- **SID**: `S-1-5-...` SDDL form
- **Integers/Hex**: `BinaryPrimitives.Read*` + direct formatting
- **Nested BinXml** (type `0x21`): recursive `ParseTopLevel()` call

## Stage 6 — Event Surfacing

`EvtxChunk` stores the rendered XML strings in a `string[]` parallel to its `Records` list. `GetEvent(int index)` pairs
them into an `EvtxEvent` record struct:

```csharp
EvtxEvent(Record: records[i], Xml: parsedXml[i], Diagnostic: ...)
```

`EvtxParser.GetEvents()` lazily yields all events across all chunks.

---

## Key Design Decisions

- **Thread-local → merge pattern** for template caches avoids lock contention during parallel chunk parsing
- **`CompiledTemplate`** amortizes the BinXml token walk — first record with a given template GUID pays the compile
  cost, all subsequent records just zip static parts + values
- **`ValueStringBuilder`** (stackalloc-backed `ref struct`) keeps the entire XML rendering path allocation-free for
  typical records under 1KB
- **Zero-copy reads** throughout: `MemoryMarshal.Read<T>`, `MemoryMarshal.Cast<byte, char>`, spans into the original
  file buffer — records never copy their event data

# EVTX -> JSON Pipeline Overview

```
byte[] fileData
 └─ EvtxParser.Parse()                    // 4-phase orchestrator
     ├─ Phase 1: Scan for valid chunk offsets (sequential)
     ├─ Phase 2: Parse chunks in parallel
     │    └─ EvtxChunk.Parse()
     │         ├─ Parse 512-byte chunk header
     │         ├─ Preload templates from chunk's hash table
     │         ├─ ReadRecords() → EvtxRecord per event
     │         └─ RenderRecords() → ParseRecordJson() per record
     │              ├─ ReadTemplateInstanceData() → substitution values
     │              ├─ CompileJsonTemplate() → CompiledJsonTemplate (cached)
     │              └─ WriteCompiledJson() → UTF-8 byte[]
     ├─ Phase 3: Collect results + sum record count
     └─ Phase 4: Attempt recovery on corrupted chunks
          └─ GetEvents() → EvtxEvent (record + JSON bytes)
```

## Step by Step

### 1. `EvtxParser.Parse()` — File Orchestration

Parses the 4096-byte file header, then computes chunk count from `(fileSize - 4096) / 65536`. Runs in 4 phases:

- **Phase 1** (sequential): Scans for the `"ElfChnk\0"` magic at each 64KB boundary. Optionally validates CRC32
  checksums. Collects valid offsets.
- **Phase 2** (parallel): `Parallel.For` over valid chunks. Each thread gets a thread-local
  `Dictionary<Guid, CompiledJsonTemplate?>` cache — avoids lock contention. After each thread finishes, its cache merges
  into the global `ConcurrentDictionary`.
- **Phase 3** (sequential): Collects `EvtxChunk` results, sums `TotalRecords`.
- **Phase 4** (parallel): Retries on chunks that lacked the magic header using `ParseHeaderless()`.

### 2. `EvtxChunk.Parse()` — Per-Chunk Work

Three sub-steps inside each 64KB chunk:

1. **Header parse**: `EvtxChunkHeader.ParseEvtxChunkHeader()` reads the 512-byte header. Contains a 64-entry common
   string table (offset 128) and a 32-entry template pointer hash table (offset 384).
2. **Template preload**: `BinXmlTemplateDefinition.PreloadFromChunk()` walks the 32-bucket chained hash table at offset
   384. Each entry is a linked list (`NextTemplateOffset`). Loads all `BinXmlTemplateDefinition` structs (GUID +
   DataSize + body offset).
3. **Record scan + render**: `ReadRecords()` scans from offset 512 looking for the `\x2a\x2a\x00\x00` record magic. Each
   hit becomes an `EvtxRecord`. Then `RenderRecords()` calls `ParseRecordJson()` on each.

### 3. `EvtxRecord` — Record Header (24 bytes)

A `readonly record struct` parsed via `MemoryMarshal.Read<RecordHeaderLayout>()`:

```
Offset 0:     uint   Signature (0x00002A2A)
Offset 4:     uint   Size
Offset 8:     ulong  EventRecordId
Offset 16:    ulong  WrittenTime (FILETIME)
Offset 24:    [BinXml event data begins]
Offset Size-4: uint  SizeCopy (trailing integrity check)
```

The key output is `EventDataFileOffset` and `EventDataLength` — a pointer into the original `byte[]` where BinXml
starts. Zero-copy via `Span<byte>`.

### 4. BinXml Token Parsing

The event data is a stream of typed tokens:

| Token                  | Value       | Meaning                                       |
|------------------------|-------------|-----------------------------------------------|
| `FragmentHeader`       | `0x0F`      | Top-level wrapper                             |
| `TemplateInstance`     | `0x0C`      | Reference to a template + substitution values |
| `OpenStartElement`     | `0x01/0x41` | Element open (`0x40` bit = has attributes)    |
| `Attribute`            | `0x06`      | Attribute name                                |
| `NormalSubstitution`   | `0x0D`      | Required value slot                           |
| `OptionalSubstitution` | `0x0E`      | Optional value slot (skip if null)            |

Almost all EVTX records use a `TemplateInstance` token. The template contains the structural skeleton (element/attribute
names) with substitution slots where per-record values get filled in.

### 5. Template System — The Key Optimization

When `ParseRecordJson()` hits a `TemplateInstance` token:

1. `ReadTemplateInstanceData()` parses the instance header (GUID, DataSize, DefDataOffset), then the
   `SubstitutionDescriptor[]` array (`ushort Size + byte Type + byte Padding` per slot). Fills `stackalloc` buffers for
   `valueOffsets`, `valueSizes`, `valueTypes` (heap only if >64 slots).
2. Cache check on `_compiledJsonCache[guid]`:
    - **Hit (non-null)**: Go straight to fast path.
    - **Hit (null)**: Template previously failed compilation → tree-walk fallback.
    - **Miss**: Call `CompileJsonTemplate()`.
3. `CompileJsonTemplate()` walks the template BinXml once and produces a `CompiledJsonTemplate`:

   ```csharp
   sealed record CompiledJsonTemplate(
       string[] Parts,      // static JSON fragments
       int[]    SubIds,     // which substitution slot fills each gap
       bool[]   IsOptional, // skip if value is null/empty?
       bool[]   InAttrValue // inside a JSON string literal?
   )
   ```

4. The template `{"#name":"EventID","#content":["<sub0>"]}` becomes
   `Parts = ["{\"#name\":\"EventID\",\"#content\":[\"", "\"]}"]` with `SubIds = [0]`. Quotes and commas are baked into
   the parts strings.

### 6. `WriteCompiledJson()` — The Hot Path

This is where most time is spent. Pure concatenation:

```csharp
vsb.Append(compiled.Parts[0]);
for (int i = 0; i < compiled.SubIds.Length; i++) {
    int subId = compiled.SubIds[i];
    WriteJsonValue(valueSizes[subId], valueTypes[subId], valueOffsets[subId], ...);
    vsb.Append(compiled.Parts[i + 1]);
}
```

`WriteJsonValue()` dispatches on the value type to format raw bytes:

- **String** (`0x01`): `MemoryMarshal.Cast<byte, char>` → JSON-escaped
- **FileTime** (`0x11`): Custom digit-by-digit ISO 8601 formatting (no `DateTime` allocation)
- **Guid** (`0x0F`): Little-endian Data1-3, big-endian bytes 8-15
- **SID** (`0x13`): SDDL format `S-1-X-...`
- **HexInt32/64**: Padded lowercase hex with `0x` prefix
- **BinXml** (`0x21`): Recursive `ParseTopLevelJson()` into a temp builder, then JSON-escape

The entire rendering uses a `ValueStringBuilder` with `stackalloc char[1024]` — most records fit without a heap
allocation.

### 7. Final Output — `EvtxEvent`

```csharp
readonly record struct EvtxEvent(
    EvtxRecord Record,            // metadata (ID, timestamp, offsets)
    string Xml,                   // empty in JSON mode
    ReadOnlyMemory<byte> Json,    // UTF-8 encoded JSON
    string? Diagnostic)           // null on success
```

The JSON output shape looks like:

```json
{"#name":"Event","#attrs":{"xmlns":"..."},"#content":[
  {"#name":"System","#content":[
    {"#name":"Provider","#attrs":{"Name":"...","Guid":"..."}},
    {"#name":"EventID","#content":["4624"]}
  ]}
]}
```

## Performance Summary

| Technique                                         | Why                                                                                    |
|---------------------------------------------------|----------------------------------------------------------------------------------------|
| Thread-local template caches                      | No lock contention during parallel chunk parsing                                       |
| Compiled templates                                | Template BinXml walked once, then pure string concat for all records sharing that GUID |
| `stackalloc` substitution buffers                 | No heap allocation for records with ≤64 value slots                                    |
| `ValueStringBuilder` (`stackalloc` + `ArrayPool`) | Most records render without touching the heap                                          |
| `MemoryMarshal.Read/Cast`                         | Zero-copy reads from the original file buffer throughout                               |
| Retain `byte[] RawData`                           | All records reference the original buffer via spans — no per-record copies             |
