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
    |-- 3. Render layer: BinXmlParser converts BinXml token stream
    |      directly to XML or UTF-8 JSON per record
    |
    v
EvtxParser { FileHeader, Chunks[], TotalRecords }
```

## Threading model

`EvtxParser.Parse()` runs in four phases:

1. **Sequential scan** — walk chunk boundaries, verify `ElfChnk\0` magic, optionally validate CRC32 checksums. Collects valid chunk offsets.
2. **Parallel parse** — `Parallel.For` dispatches each valid chunk to a thread. Chunks are self-contained (own header, own string/template caches), so threads share nothing except a read-only file buffer and a `ConcurrentDictionary<Guid, CompiledTemplate?>`.
3. **Sequential collect** — gather parsed chunks and sum record counts.
4. **Parallel recovery** — chunks with destroyed headers are scanned for `0x2A2A` record magic to recover individual records.

The `maxThreads` parameter maps directly to `ParallelOptions.MaxDegreeOfParallelism`.

## Template compilation

BinXml templates appear once per chunk but are referenced by many records. On first encounter, the parser compiles a template into:

- **Static string fragments** — the fixed XML parts between substitution slots
- **Substitution metadata** — slot IDs, optional flags

Compiled templates are cached by GUID in a `ConcurrentDictionary` shared across chunks. Subsequent records skip all template parsing and just interleave static parts with formatted substitution values.

`WevtCache` takes this further — it pre-extracts templates from Windows PE binaries (via WEVT_TEMPLATE resources) before any records are parsed.

## Corruption resilience

- Chunks with invalid `ElfChnk\0` signatures are not discarded — Phase 4 scans their 64KB region for `0x2A2A` record magic and recovers individual records
- Records that fail BinXml parsing (`ArgumentOutOfRangeException`) produce empty output rather than crashing the parser
- CRC32 validation is opt-in (`validateChecksums: true`) — when enabled, chunks failing header or data checksums are skipped entirely

## Key types

| Type | Role |
|------|------|
| `EvtxParser` | Entry point. Orchestrates parsing, holds results. |
| `EvtxChunk` | Self-contained 64KB chunk with header, records, and rendered output. |
| `BinXmlParser` | Converts BinXml token streams to XML or JSON. One instance per chunk. |
| `CompiledTemplate` | Pre-compiled template: static parts + substitution slots. |
| `WevtCache` | Offline template cache from Windows PE binaries. |

## References

- [libevtx format documentation](https://github.com/libyal/libevtx/blob/main/documentation/Windows%20XML%20Event%20Log%20(EVTX).asciidoc) — comprehensive binary format spec
- [omerbenamram/evtx](https://github.com/omerbenamram/evtx) — Rust parser (error handling strategy inspiration)
