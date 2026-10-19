# Architecture

## Parsing pipeline

File bytes flow through three layers:

```
path / byte[] / seekable Stream  (ChunkSource)
    |
    v
EvtxParser.Parse / ReadEvents / ReadEventsAsync / WriteTo
    |
    |-- 1. File layer: parse the 4 KB header, list the 64 KB chunk slots
    |
    |-- 2. Chunk layer (parallel): each worker loads a chunk (a slice of the
    |      byte[], or a positional read into its own 64 KB buffer), checks
    |      the ElfChnk magic (and CRC32 when asked), walks event records
    |
    |-- 3. Render layer: BinXmlParser converts each record's BinXml token
    |      stream directly to UTF-8 XML or JSON, appended to one buffer
    |      per chunk (no UTF-16 strings in between)
    |
    v
EvtxLog { Header, Chunks[], RecordCount, Events, Diagnostics }   (or a stream of events, or bytes)
```

Every entry point runs the same two passes over the same pipeline, so they classify chunks identically and produce
events (or bytes) in the same order:

| Entry point | Output |
|-------------|--------|
| `Parse` | Collects every `EvtxChunk` into an `EvtxLog`. Each chunk keeps its records plus one UTF-8 buffer of rendered output; an `EvtxEvent` is a 16-byte view (chunk + index) over it. |
| `ReadEvents` / `ReadEventsAsync` | Yields each chunk's events as soon as the chunk is next in order; only a few chunks per worker are alive at once. |
| `WriteTo` | Doesn't build `EvtxChunk`s: each chunk is rendered into a reusable buffer (a line feed after each record) and written to the output `Stream` as soon as it is next in order. |

## Threading model

`ChunkPipeline` runs one piece of work per chunk slot on `MaxThreads` workers and hands results to the calling thread
in slot order. Workers claim slots in order and fill a bounded ring of result slots (4 per worker); the consumer takes
each slot as soon as it's next and releases it for reuse, so loading, rendering and consuming overlap with no batch
barrier and memory stays flat. With one worker the work runs inline on the calling thread. Chunks are self-contained
(own header, own string/template tables), so workers share nothing except the read-only input. Each worker keeps its
own compiled-template cache for its whole run.

1. **Pass 1** — every chunk slot in file order: a slot without the `ElfChnk\0` magic is marked for recovery; with
   `ValidateChecksums`, a chunk failing CRC32 is skipped (a `ChunkChecksumMismatch` diagnostic); every other chunk is
   parsed and rendered.
2. **Pass 2** — the marked slots, in file order, with fresh workers (so fresh template caches): each 64 KB region is
   scanned for `0x2A2A` record magic and the records found are rendered. Only records that produce output are kept;
   these chunks have `Recovered = true`.

## Template compilation

BinXml templates appear once per chunk but are referenced by many records. On first encounter, the parser compiles a template into:

- **Static fragments** — the fixed XML/JSON parts between substitution slots, pre-encoded as UTF-8 (JSON
  templates also keep an escaped copy for BinXml embedded inside string values), so rendering a warm record is mostly
  copying bytes
- **Substitution metadata** — slot IDs, optional flags

Compiled templates are cached by GUID in a per-thread dictionary, so each worker compiles a template the first time it meets it and reuses it for every later chunk on that thread. Subsequent records skip all template parsing and just interleave static parts with formatted substitution values.

Every template, whether embedded in a chunk or a provider template from `WevtCache`, goes through one compiler (`TemplateProgramCompiler`). It produces a format-neutral `TemplateProgram`, a flat list of element, attribute, text and substitution ops. The two template sources differ only in how names are stored: name-table offsets versus WEVT inline names.

Output is produced by one format writer per format (`XmlWriter`, `JsonWriter` in `BinXmlDecoder.cs`; value types, so each format gets its own specialised copy of the code that drives them). Two things drive the writers:

- **The decoder** walks BinXml tokens directly: a record's own token stream, embedded BinXml values (type 0x21), and template bodies that aren't compiled. It is strict: corrupt structure fails the record (see below).
- **Template compilation** replays a `TemplateProgram` through the same writer with every value left out; the writer's output between values becomes the template's static UTF-8 parts. So compiled and decoded output follow one set of rules by construction, and `CompiledDecoderParityTests` checks it on every sample and seeded mutations. A template whose names or text contain surrogate code units isn't split into parts (a surrogate at a part's edge could pair with a neighbouring value) and is decoded instead.

Substitution values are formatted by one type switch (`BinXmlValueWriter.cs`); the writer supplies only string escaping, hex-integer padding, and how embedded BinXml is rendered.

`WevtCache` holds templates extracted from Windows PE binaries (WEVT_TEMPLATE resources), keyed by GUID. It's a fallback: the writers use a provider template only when a record's embedded template is missing (size 0), runs past its chunk, or doesn't compile. A usable embedded template is never replaced.

## Typed fields

`TemplateFields` (built from a template's `TemplateProgram` the first time a parse reads its fields) records every
attribute value and leaf-element content with the element path leading to it, and interprets it per position in the
event: `Event/System/EventID` content is the EventID, `Event/System/Provider@Name` the provider, a leaf under
`Event/EventData` a pair named by its `Name` attribute, a leaf under `Event/UserData` a pair named by the element.
Content holding an embedded BinXml value (type 0x21; Security logs keep EventData in its own template) is followed into
that value's template instance with the position of the element holding it. Values that are literal template text
(providers, channels, often levels) are parsed once per template; substitutions are read by type per record. A BinXml
stream without a template instance (CAPI2 stores UserData this way) has no substitution values, so it is decoded to
XML with the shared decoder and its fields are read from that.

Fields are read as their own pass before a record is rendered (or instead of rendering, with `FieldsOnly`), so the
rendering code is the same whether or not fields are read. Templates keep their program only in parses that read
fields: a retained program per template grows every worker's live heap, which costs parallel parses wall time.
`EventFieldsTests` checks every field and pair of every sample record against the record's own XML parsed with
System.Xml.

## Compact JSON

`OutputFormat.Json` is written from a per-template `JsonPlan` (built on first use from the template's program, cached
on its `CompiledTemplate` per parent kind): keys pre-escaped, child groups and arrays decided, literal values
pre-written, numeric System fields marked. Per record the plan writer walks it with the substitution values, deciding
only what depends on the record: optional attributes, and embedded BinXml (nested in place through the nested
template's plan). Templates whose shape varies more (a substituted EventData `Name`, text mixed with substitutions,
literal text beside child elements), records without a template, embedded keys colliding with siblings and salvage
cases go through `JsonTree` instead: the record is built into a small pooled tree (from the program, or from the
decoder's XML) and written by the same rules. `FlatJsonTests` checks that the two writers give identical bytes for
every sample record, and that the output matches the record's XML.

## Filtering

`EvtxFilter` is compiled once per instance into a `RecordFilter` (bitsets for event IDs and levels, a case-insensitive
provider set, UTC ticks), cached by reference. Per chunk, the header's record-ID range can skip the chunk outright
(`SkipsChunk`; a dirty chunk's last ID isn't trusted). Per record, the record-ID range is checked from the record
header, then, if the filter needs them, the System fields pass runs and is checked; records that fail are never
rendered. `EvtxFilterTests` checks that a filtered parse (and `WriteTo`) equals the unfiltered parse filtered
afterwards, for every sample and a range of filters.

## Corruption resilience

- Chunks with invalid `ElfChnk\0` signatures are not discarded — pass 2 scans their 64KB region for `0x2A2A` record magic and recovers individual records
- Records whose BinXml is corrupt fail on their own with an `EvtxDiagnostic` and empty output, rather than crashing the parser or rendering junk. With `Salvage = true` they keep their well-formed prefix instead (decoding stops at the corruption and the writers close every open element)
- CRC32 validation is opt-in (`ValidateChecksums = true`) — when enabled, chunks failing header or data checksums are skipped entirely

## Key types

| Type | Role |
|------|------|
| `EvtxParser` | Static entry points (`Parse`, `ReadEvents`, `ReadEventsAsync`, `WriteTo`, `ParseChunk`, `ParseFileHeader`). |
| `EvtxOptions` | Immutable options record shared by every entry point. |
| `EvtxLog` | Result of `Parse`: file header, chunks, record count, diagnostics. |
| `EvtxChunk` | Self-contained 64KB chunk with header, records, and one UTF-8 buffer of rendered output. |
| `EvtxEvent` | 16-byte view of one record of a chunk: record ID, written time, file offset, output, diagnostic. |
| `ChunkSource` | Where chunk bytes come from: a `byte[]`, positional reads of a file handle, or a locked seekable stream. |
| `ChunkPipeline` | Runs per-chunk work on workers and hands results to one consumer in order (bounded ring). |
| `TemplateFields` | A template's field positions, interpreted into typed System fields and EventData/UserData pairs. |
| `FieldSink` / `ChunkFields` | Collect a chunk's fields per record, then hold them in exact-size arrays. |
| `BinXmlParser` | Converts BinXml token streams to UTF-8 XML or JSON. One instance per chunk. |
| `CompiledTemplate` | A template compiled for one format: UTF-8 static parts + substitution slots (one type and one executor for XML and JSON). |
| `ValueUtf8Builder` | Stack-first, `ArrayPool`-backed UTF-8 builder all rendering goes through. Transcodes UTF-16 text from the file as it's appended, and gives the same bytes as encoding the whole record once (unpaired surrogates become U+FFFD). |
| `BinXmlValueFormatter` | Formats substitution values (FILETIME, GUID, SID, hex, numbers) straight to UTF-8 with the invariant culture. |
| `TemplateProgram` | Format-neutral compiled template (ops), turned into a `CompiledTemplate` per format by replaying it through that format's writer. |
| `WevtCache` | Provider templates from Windows PE binaries, used as a fallback for missing or corrupt embedded templates. |

## References

- [libevtx format documentation](https://github.com/libyal/libevtx/blob/main/documentation/Windows%20XML%20Event%20Log%20(EVTX).asciidoc) — comprehensive binary format spec
- [omerbenamram/evtx](https://github.com/omerbenamram/evtx) — Rust parser (error handling strategy inspiration)
