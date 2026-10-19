# Performance

## Choosing an entry point

| Method | Use when | Memory |
|--------|----------|--------|
| `EvtxParser.WriteTo` | You only need the rendered output (export, piping, hashing) | Flat: from a path or stream, a few reusable buffers per worker and the file is never loaded whole; nothing retained per record |
| `EvtxParser.ReadEvents` / `ReadEventsAsync` | You want to process events one by one as they're parsed | Flat: a few chunks per worker (workers wait when the consumer falls behind); events you keep keep their chunk alive |
| `EvtxParser.Parse` | You need the whole log at once: random access to chunks and events, or all diagnostics | Every chunk's rendered output held in memory (from a path, the file itself is not) |

`WriteTo` is what the benchmark CLI (`perf/AxoParse.Bench`) uses. Its output is the record bytes `Parse` renders, each
followed by a line feed (the tests check this for every sample file, format, and thread count).

## Threading

| `EvtxOptions.MaxThreads` | Behaviour |
|--------------------------|-----------|
| `0` or negative | Use all available cores (default) |
| `1` | Single-threaded — useful for debugging or constrained environments |
| `N` | Use exactly N threads |

Each 64KB chunk is independent, so chunks are rendered in parallel with no shared mutable state (each worker has its
own compiled-template cache). Speed-up is sub-linear: the file scan, reading the input and the in-order output are
sequential, and process start-up is a fixed cost. On the 30 MB benchmark file, the CLI with 8 threads is about 2.6×
faster than with 1. For small files (1-2 chunks), single-threaded may be faster due to
thread pool overhead.

## Memory

The `byte[]` overloads take the whole file in memory. The path and stream overloads read each 64 KB chunk on demand
instead, into a per-worker buffer.

Each chunk's records are rendered into one exact-size UTF-8 buffer. `EvtxEvent.Utf8` is a slice of that buffer, so
there's no per-record array, and an `EvtxEvent` itself is a 16-byte struct (chunk + index). `GetText()` decodes to a
`string` only when called.

Every entry point runs as a pipeline: workers claim chunks in file order, read (for the path/stream overloads) and
render each into a slot of a small ring (4 per worker), while the calling thread consumes each chunk as soon as it's
next in order (writes it, or yields its events) and hands the slot back. Reading, rendering and writing overlap, and no worker waits for a whole batch to
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
  Short ASCII text with nothing to escape (most names and values) skips the vectorised search and the transcoder and
  is copied one byte per character; past 16 characters the vectorised path wins.
- Integers are formatted with `Utf8Formatter`, which is culture-free (output doesn't depend on the machine's locale)
  and skips the `NumberFormatInfo` lookup that `TryFormat` pays on every call.
- A chunk's records are in time order, so FILETIME formatting remembers the last calendar date and skips the
  date arithmetic when the day repeats.

The two formats run at similar speeds. XML is slightly faster single-threaded on the benchmark file.

## Template compilation

Templates are the biggest performance lever. A typical EVTX file has a handful of unique templates referenced by thousands of records. The compiled template cache means:

- First record with a given template: full BinXml tree walk + compilation
- All subsequent records: interleave static string fragments with formatted values (no parsing)

Each worker thread keeps its own compiled-template cache, so a template is compiled at most once per thread. Within a
chunk, each template definition is also resolved once (its GUID, size, compiled form or fallback) and remembered by
its offset, so later instances skip both the definition lookup and the cache lookup. An instance's values are read in
one pass: their sizes and types stay in the record's descriptor array, and only their offsets are computed.

## Benchmarks

- **Cross-parser comparison** (wall clock via hyperfine, against Rust `evtx`, libevtx, and a JS parser):
  `tools/bench/bench-all.sh`. Latest results are in the [README](../README.md#benchmark-results) and
  `tools/bench/benchmark-comparison.md`. See `tools/bench/scripts/README.md` for prerequisites.
- **Internal micro/chunk/file benchmarks** (BenchmarkDotNet, for before/after comparisons of a change):
  [`perf/AxoParse.Benchmarks`](../perf/AxoParse.Benchmarks/README.md).
