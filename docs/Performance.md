# Performance

## Choosing an entry point

| Method | Use when | Memory |
|--------|----------|--------|
| `EvtxParser.WriteTo` | You only need the rendered output (export, piping, hashing) | Flat: from a path or handle, a few reusable buffers per worker and the file is never loaded whole; nothing retained per record |
| `EvtxParser.ParseAsync` | You want to start consuming events before the whole file is parsed | Holds only chunks not yet consumed (grows toward the whole file if the consumer is slower than the parser) |
| `EvtxParser.Parse` | You need random access to chunks, records, templates, or diagnostics | Whole file's output held in memory |

`WriteTo` is what the benchmark CLI (`perf/AxoParse.Bench`) uses. Its output is byte-identical to concatenating `Parse`
output (the tests check this for every sample file, format, and thread count).

## Threading

| `maxThreads` | Behaviour |
|--------------|-----------|
| `0` or `-1` | Use all available cores (default) |
| `1` | Single-threaded — useful for debugging or constrained environments |
| `N` | Use exactly N threads |

Each 64KB chunk is independent, so chunks are rendered in parallel with no shared mutable state (each worker has its
own compiled-template cache). Speed-up is sub-linear: the file scan, reading the input and the in-order output are
sequential, and process start-up is a fixed cost. On the 30 MB benchmark file, the CLI with 8 threads is about 2.6×
faster than with 1. For small files (1-2 chunks), single-threaded may be faster due to
thread pool overhead.

## Memory

`Parse`, `ParseAsync` and `WriteTo(byte[], ...)` take the whole file as a `byte[]`. `WriteTo(path, ...)` and
`WriteTo(SafeFileHandle, ...)` read each 64 KB chunk on demand instead.

`Parse` renders each chunk's records into one exact-size UTF-8 buffer. `ParsedUtf8` and `ParsedJson` are slices of that
buffer, so there's no per-record array. `ParsedXml` decodes a record to `string` only on first access, so if you
don't read it, it costs nothing.

`WriteTo` runs as a pipeline: workers claim chunks in file order, read (for the path/handle overloads) and render each
into a buffer from a small ring (4 per worker), while the calling thread writes each chunk as soon as it's next in
order and hands its buffer back. Reading, rendering and writing overlap, and no worker waits for a whole batch to
finish. Streaming from a path, the benchmark CLI peaks at about 8 MiB on the 30 MB sample single-threaded and 18 MiB
with 8 threads (39 and 49 MiB when it loaded the file first).

## XML vs JSON

Both formats are rendered directly from the BinXml token stream to UTF-8 — JSON is **not** converted from XML, and
neither goes through a UTF-16 `string`.

- Rendering uses `ValueUtf8Builder`, a stack-first, `ArrayPool`-backed UTF-8 builder. UTF-16 text from the file
  (names, string values) is transcoded as it's appended.
- Compiled templates store their static fragments pre-encoded as UTF-8, so warm records are mostly `memcpy` plus value
  formatting.
- JSON escaping is vectorised. Embedded BinXml values are written pre-escaped instead of rendered and then escaped.
- Numbers are formatted with the invariant culture, so output doesn't depend on the machine's locale.

The two formats run at similar speeds. XML is slightly faster single-threaded on the benchmark file.

## Template compilation

Templates are the biggest performance lever. A typical EVTX file has a handful of unique templates referenced by thousands of records. The compiled template cache means:

- First record with a given template: full BinXml tree walk + compilation
- All subsequent records: interleave static string fragments with formatted values (no parsing)

Each worker thread keeps its own compiled-template cache, so a template is compiled at most once per thread.

## Benchmarks

- **Cross-parser comparison** (wall clock via hyperfine, against Rust `evtx`, libevtx, and a JS parser):
  `tools/bench/bench-all.sh`. Latest results are in the [README](../README.md#benchmark-results) and
  `tools/bench/benchmark-comparison.md`. See `tools/bench/scripts/README.md` for prerequisites.
- **Internal micro/chunk/file benchmarks** (BenchmarkDotNet, for before/after comparisons of a change):
  [`perf/AxoParse.Benchmarks`](../perf/AxoParse.Benchmarks/README.md).
