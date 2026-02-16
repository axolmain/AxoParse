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
- **Records that fail BinXml parsing** — produce empty output (`""` for XML, `[]` for JSON) rather than throwing
- **CRC32 checksum failures** — only enforced when `validateChecksums: true`. When enabled, failing chunks are skipped entirely

The design philosophy is: throw on "this isn't an EVTX file" errors, recover silently from "this EVTX file has corruption" errors.
