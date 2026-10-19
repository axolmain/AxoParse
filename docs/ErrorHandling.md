# Error Handling

## EvtxParseException

Structural validation errors throw `EvtxParseException` with an `ErrorCode` property for programmatic handling:

```csharp
try
{
    EvtxParser parser = EvtxParser.Parse(fileData);
}
catch (EvtxParseException ex) when (ex.ErrorCode == EvtxParseError.FileHeaderTooShort)
{
    // File is truncated — not enough data for the 128-byte header
}
catch (EvtxParseException ex) when (ex.ErrorCode == EvtxParseError.InvalidFileSignature)
{
    // Not an EVTX file — "ElfFile\0" magic not found
}
```

### Error codes

| ErrorCode | Thrown by | Meaning |
|-----------|----------|---------|
| `FileHeaderTooShort` | `EvtxFileHeader.ParseEvtxFileHeader` | Data shorter than 128 bytes |
| `InvalidFileSignature` | `EvtxFileHeader.ParseEvtxFileHeader` | Missing `ElfFile\0` magic |
| `ChunkHeaderTooShort` | `EvtxChunkHeader.ParseEvtxChunkHeader` | Chunk data shorter than 512 bytes |
| `InvalidChunkSignature` | `EvtxChunkHeader.ParseEvtxChunkHeader` | Missing `ElfChnk\0` magic |

## What doesn't throw

Most corruption is handled silently to maximise data recovery:

- **Chunks with bad magic** — skipped during Phase 1, then Phase 4 scans the 64KB region for individual records by looking for `0x2A2A` record magic
- **Records that fail BinXml parsing** — produce empty output (`""` for XML, a zero-length slice for JSON) rather than throwing. The reason is in `EvtxEvent.Diagnostic` / `EvtxChunk.RenderDiagnostics`. `WriteTo` writes nothing for such records and doesn't report diagnostics. Decoding is strict, as in the Rust `evtx` crate: an unknown token, nesting deeper than 64 levels, a name running past the data, a substitution outside a template, an element inside an attribute value, or a value running past its chunk fails the record instead of rendering whatever bytes follow.
- **Salvage mode** (`salvage: true`) — a record whose BinXml structure is corrupt keeps the output decoded before the corruption instead of failing. Decoding stops there and every open element (and JSON object or array) is closed, so the output is still well-formed. The record still has a diagnostic (`BinXml salvaged …`), so `IsSuccess` is false. Errors outside the token structure, such as a substitution value running past its chunk, still fail the record.
- **CRC32 checksum failures** — only enforced when `validateChecksums: true`. When enabled, failing chunks are skipped entirely. Chunks flagged `NoCrc32` carry no checksums and always pass. The file header's checksum is read but not validated
- **Implausible sizes and offsets from the file** — a chunk's record ID range is only used as a capacity hint and ignored when it's inverted or too large, and a template instance claiming more substitutions than its record can hold fails just that record, before anything is allocated from the count

The design philosophy is: throw on "this isn't an EVTX file" errors, recover silently from "this EVTX file has corruption" errors.
