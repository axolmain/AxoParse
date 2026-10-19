# Architecture

## Parsing pipeline

Raw file bytes flow through three layers:

```
byte[] fileData
    |
    v
EvtxParser.Parse()
    |
    |-- 1. File layer: validate 4KB header, scan for valid 64KB chunks
    |
    |-- 2. Chunk layer (parallel): each chunk parses its own header,
    |      preloads string cache + template table, walks event records
    |
    |-- 3. Render layer: BinXmlParser converts each record's BinXml token
    |      stream directly to UTF-8 XML or JSON, appended to one buffer
    |      per chunk (no UTF-16 strings in between)
    |
    v
EvtxParser { FileHeader, Chunks[], TotalRecords }
```

There are three entry points over the same pipeline:

| Entry point | Output |
|-------------|--------|
| `EvtxParser.Parse` | Holds every chunk. Each `EvtxChunk` keeps its records plus one UTF-8 buffer of rendered output, exposed per record as `ParsedUtf8` / `ParsedJson` slices or as lazily decoded `ParsedXml` strings. |
| `EvtxParser.ParseAsync` | Same phases, but parsed chunks go through a channel and a reorder buffer, so `EvtxEvent`s are yielded in file order as soon as each chunk is ready. |
| `EvtxParser.WriteTo` | Same chunk classification (done by the workers, so checksum validation runs in parallel), but doesn't build `EvtxChunk`s. Workers claim chunks in order, load each (a slice of the `byte[]`, or a positional read of the file into a per-worker 64 KB buffer) and render it into a buffer from a bounded ring. The calling thread writes each chunk to the output `Stream` as soon as it's next in file order and returns the buffer, so rendering and writing overlap with no batch barrier. Output bytes match `Parse`. |

## Threading model

`EvtxParser.Parse()` runs in four phases:

1. **Sequential scan** — walk chunk boundaries, verify `ElfChnk\0` magic, optionally validate CRC32 checksums. Collects valid chunk offsets.
2. **Parallel parse** — `Parallel.For` dispatches each valid chunk to a thread. Chunks are self-contained (own header, own string/template caches), so threads share nothing except the read-only file buffer. Each worker has its own compiled-template cache.
3. **Sequential collect** — gather parsed chunks and sum record counts.
4. **Parallel recovery** — chunks with destroyed headers are scanned for `0x2A2A` record magic to recover individual records.

The `maxThreads` parameter maps directly to `ParallelOptions.MaxDegreeOfParallelism`.

## Template compilation

BinXml templates appear once per chunk but are referenced by many records. On first encounter, the parser compiles a template into:

- **Static fragments** — the fixed XML/JSON parts between substitution slots, pre-encoded as UTF-8 (JSON
  templates also keep an escaped copy for BinXml embedded inside string values), so rendering a warm record is mostly
  copying bytes
- **Substitution metadata** — slot IDs, optional flags

Compiled templates are cached by GUID in a per-thread dictionary, so each worker compiles a template the first time it meets it and reuses it for every later chunk on that thread. Subsequent records skip all template parsing and just interleave static parts with formatted substitution values.

Every template, whether embedded in a chunk or a provider template from `WevtCache`, goes through one compiler (`TemplateProgramCompiler`). It produces a format-neutral `TemplateProgram`, a flat list of element, attribute, text and substitution ops, which the XML and JSON writers lower into their compiled forms. The two template sources differ only in how names are stored: name-table offsets versus WEVT inline names.

Anything that isn't a compiled template goes through one decoder (`BinXmlDecoder.cs`): a record's own token stream, embedded BinXml values (type 0x21) and templates that don't compile. It walks the tokens once and calls a format writer (`XmlWriter`, `JsonWriter`; value types, so each format gets its own specialised copy), which follows the same output rules as the compiled templates. The decoder is strict: corrupt structure fails the record (see below).

`WevtCache` holds templates extracted from Windows PE binaries (WEVT_TEMPLATE resources), keyed by GUID. It's a fallback: the writers use a provider template only when a record's embedded template is missing (size 0), runs past its chunk, or doesn't compile. A usable embedded template is never replaced.

## Corruption resilience

- Chunks with invalid `ElfChnk\0` signatures are not discarded — Phase 4 scans their 64KB region for `0x2A2A` record magic and recovers individual records
- Records whose BinXml is corrupt fail on their own with a diagnostic and empty output, rather than crashing the parser or rendering junk. With `salvage: true` they keep their well-formed prefix instead (decoding stops at the corruption and the writers close every open element)
- CRC32 validation is opt-in (`validateChecksums: true`) — when enabled, chunks failing header or data checksums are skipped entirely

## Key types

| Type | Role |
|------|------|
| `EvtxParser` | Entry point. Orchestrates parsing, holds results. |
| `EvtxChunk` | Self-contained 64KB chunk with header, records, and one UTF-8 buffer of rendered output. |
| `BinXmlParser` | Converts BinXml token streams to UTF-8 XML or JSON. One instance per chunk. |
| `CompiledTemplate` / `CompiledJsonTemplate` | Pre-compiled template: UTF-8 static parts + substitution slots. |
| `ValueUtf8Builder` | Stack-first, `ArrayPool`-backed UTF-8 builder all rendering goes through. Transcodes UTF-16 text from the file as it's appended, and gives the same bytes as encoding the whole record once (unpaired surrogates become U+FFFD). |
| `BinXmlValueFormatter` | Formats substitution values (FILETIME, GUID, SID, hex, numbers) straight to UTF-8 with the invariant culture. |
| `TemplateProgram` | Format-neutral compiled template (ops), lowered once per format into `CompiledTemplate` / `CompiledJsonTemplate`. |
| `WevtCache` | Provider templates from Windows PE binaries, used as a fallback for missing or corrupt embedded templates. |

## References

- [libevtx format documentation](https://github.com/libyal/libevtx/blob/main/documentation/Windows%20XML%20Event%20Log%20(EVTX).asciidoc) — comprehensive binary format spec
- [omerbenamram/evtx](https://github.com/omerbenamram/evtx) — Rust parser (error handling strategy inspiration)
