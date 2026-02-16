# Performance

## Threading

| `maxThreads` | Behaviour |
|--------------|-----------|
| `0` or `-1` | Use all available cores (default) |
| `1` | Single-threaded — useful for debugging or constrained environments |
| `N` | Use exactly N threads |

Each 64KB chunk is independent, so parallelism scales linearly with chunk count up to core count. For small files (1-2 chunks), single-threaded may be faster due to thread pool overhead.

## Memory

The parser currently loads the entire file into a `byte[]`. The parsed chunks, records, and rendered XML/JSON strings are all held in memory alongside the raw buffer.

For a rough estimate: a 30MB EVTX file with ~15,000 records produces ~50-70MB total memory usage (raw bytes + rendered output).

[Todo] Streaming/memory-mapped file support is planned — see Roadmap.

## XML vs JSON

Both formats are rendered directly from the BinXml token stream — JSON is **not** converted from XML.

- **XML** — rendered via `ValueStringBuilder` (stack-allocated, grows to heap as needed)
- **JSON** — rendered via `Utf8JsonWriter` writing directly to UTF-8 bytes

JSON output skips XML escaping and entity encoding, so it's slightly faster for large files. The difference is small (~5-10%) since BinXml parsing dominates.

## Template compilation

Templates are the biggest performance lever. A typical EVTX file has a handful of unique templates referenced by thousands of records. The compiled template cache means:

- First record with a given template: full BinXml tree walk + compilation
- All subsequent records: interleave static string fragments with formatted values (no parsing)

`WevtCache` pushes this further by pre-compiling templates before any records are parsed.

## Benchmarks

[Todo] Link to benchmark methodology and latest results. Current numbers are in the README.
