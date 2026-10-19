# Error Handling

## EvtxParseException

Structural validation errors throw `EvtxParseException` with an `ErrorCode` property for programmatic handling:

```csharp
try
{
    EvtxLog log = EvtxParser.Parse(fileData);
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
| `FileHeaderTooShort` | every file entry point, `ParseFileHeader` | Data shorter than 128 bytes |
| `InvalidFileSignature` | every file entry point, `ParseFileHeader` | Missing `ElfFile\0` magic |
| `InvalidChunkSignature` | `ParseChunk` | Missing `ElfChnk\0` magic |

`Parse`, `WriteTo` and the `byte[]` overloads of `ReadEvents` / `ReadEventsAsync` throw these when called; the path
overloads of `ReadEvents` / `ReadEventsAsync` open the file when enumeration starts, so they throw on the first
`MoveNext`. `ParseChunk` throws `ArgumentException` for input shorter than 64 KB, and `WriteTo(Stream, ...)` for an
input stream that can't read or seek. A worker failure other than these (an I/O error, say) surfaces as an
`AggregateException` when parsing on several threads.

## What doesn't throw

Most corruption is handled without throwing, to maximise data recovery. Problems are reported as `EvtxDiagnostic`
values (`Kind`, `RecordId`, `FileOffset`, `Message`) on the affected event and in `EvtxLog.Diagnostics`:

- **Chunks with bad magic** — set aside in the first pass, then the second pass scans the 64KB region for individual records by looking for `0x2A2A` record magic (`EvtxChunk.Recovered` is true for these)
- **Records that fail BinXml parsing** — produce empty output rather than throwing, with a `RecordRenderFailed` diagnostic in `EvtxEvent.Diagnostic`. `WriteTo` writes nothing for such records (not even a line feed) and doesn't report diagnostics. Decoding is strict, as in the Rust `evtx` crate: an unknown token, nesting deeper than 64 levels, a name running past the data, a substitution outside a template, an element inside an attribute value, or a value running past its chunk fails the record instead of rendering whatever bytes follow.
- **Salvage mode** (`Salvage = true`) — a record whose BinXml structure is corrupt keeps the output decoded before the corruption instead of failing. Decoding stops there and every open element (and JSON object or array) is closed, so the output is still well-formed. The record still has a `RecordSalvaged` diagnostic (`BinXml salvaged …`), so `IsSuccess` is false and `IsSalvaged` is true. Errors outside the token structure, such as a substitution value running past its chunk, still fail the record.
- **CRC32 checksum failures** — only enforced when `ValidateChecksums = true`. When enabled, failing chunks are skipped entirely, each with a `ChunkChecksumMismatch` diagnostic in `EvtxLog.Diagnostics`. Chunks flagged `NoCrc32` carry no checksums and always pass. The file header's checksum is read but not validated
- **Implausible sizes and offsets from the file** — a chunk's record ID range is only used as a capacity hint and ignored when it's inverted or too large, and a template instance claiming more substitutions than its record can hold fails just that record, before anything is allocated from the count

The design philosophy is: throw on "this isn't an EVTX file" errors, recover silently from "this EVTX file has corruption" errors.
