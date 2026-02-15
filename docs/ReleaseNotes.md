# Release Notes

## v0.1.0 — Initial Release

High-performance .NET library for parsing Windows Event Log (.evtx) files.

### Features

- **Full EVTX parsing** — file headers, 64KB chunks, and individual event records
- **BinXml decompilation** — converts binary XML token streams to standard XML or UTF-8 JSON
- **Template pre-compilation** — compiles BinXml templates once for zero overhead on repeated renders
- **WEVT template cache** — extracts and caches templates from Windows PE binaries (DLLs/EXEs) for offline parsing
- **Parallel chunk processing** — configurable thread count for multi-core throughput
- **CRC32 validation** — optional checksum verification on chunks
- **Resilient parsing** — gracefully handles corrupted records, bad checksums, and malformed chunks

### Performance

- Span-based zero-copy parsing throughout
- Stack allocations and value types to minimise GC pressure
- No LINQ in hot paths
- Pre-compiled templates avoid repeated parsing overhead

### Dependencies

- [PeNet](https://github.com/secana/PeNet) — PE binary resource extraction for WEVT templates
