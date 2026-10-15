# Optimisation lenses

Each section is one agent's brief. Paste the whole section into that agent's prompt. The items are things to look for, not a checklist to tick off: confirm each candidate is on a hot path and that the cost is real before proposing it.

---

## Lens 1: Algorithmic and redundant work

You look for work that shouldn't happen at all, or happens more often than needed. These are the biggest wins, and they're worth proposing even on 🧊 paths.

- **Rescans:** the same span walked twice (e.g. one pass to measure, another to copy; `IndexOf` followed by a manual loop over the same range; validate-then-parse that reads every field twice).
- **Repeated lookups:** the same dictionary key looked up several times in one path (`ContainsKey` + indexer → `TryGetValue`, or `CollectionsMarshal.GetValueRefOrAddDefault` for get-or-add); the same name or offset resolved per record when it could be resolved once per chunk or template.
- **Linear search where an index fits:** `List.Contains`/`IndexOf` in loops; `switch` on strings in hot code; searching templates or names by scanning.
- **Missed caching across records/chunks:** work that depends only on (template GUID, format), (chunk, name offset) or static data, but is recomputed per record. Check whether existing caches (`_nameCache`, compiled template caches, WEVT cache) are hit, sized and keyed well, and whether thread-local caches in `Parallel.For` cause repeated compilation per thread that a warm shared cache would avoid.
- **Early exit:** loops that keep going after the answer is known; full parses where a header peek would decide.
- **Quadratic patterns:** string/array growth by concatenation, `Insert(0, …)`, `RemoveAt` in loops, nested loops over records × templates.
- **Parallel scaling:** chunk-level work serialised behind a lock or a shared `ConcurrentDictionary` hot key; an imbalanced partition; phase 1 (sequential scan) doing work that phase 2 could do in parallel; results materialised and then re-walked.
- **Output materialisation:** rendering to `string` and then re-encoding to UTF-8, or building an intermediate and copying it to the final buffer when it could be written directly.

Cost to name: "O(x) → O(y)", "N lookups → 1", "recomputed per record → once per template".

---

## Lens 2: Allocations and GC pressure

You look for heap allocations on 🔥/♨️ paths. A Gen0 collection costs far more than the allocation itself, and Gen1 promotions (seen in `ChunkBenchmarks`) mean objects survive across a chunk.

- `new T[]`, `new List<T>()`, `new Dictionary<,>()` inside per-record or per-value code. Use `stackalloc` (fixed small cap), `ArrayPool<T>.Shared`, a reusable per-parser buffer, or `[ThreadStatic]`/per-chunk scratch.
- Collections without a capacity when the count is known (`new List<T>(expected)`), and `ToArray()`/`ToList()` copies made only to return a value.
- **Strings:** `Substring`, `string.Concat`/`+`/interpolation, `ToString()`, `string.Format`, `Encoding.GetString` in hot code; `new string(span)` for a value that's only appended somewhere else. Prefer appending spans to `ValueStringBuilder`, `ISpanFormattable.TryFormat`, or `Utf8Formatter`. Check whether a `string` is created only to be written again (the XML path builds `string` per record; is that required by the public API, or only by an internal step?).
- **Boxing:** value types passed as `object`/interfaces, `Enum.ToString()`, `HasFlag` on old patterns, struct `Equals`/`GetHashCode` without `IEquatable<T>`, dictionary keys that are structs without proper equality.
- **Closures and delegates:** lambdas that capture locals in hot code (each call allocates a closure plus a delegate); `Parallel.For` bodies capturing more than they need; LINQ anywhere in 🔥/♨️ code.
- **Iterators/async:** `IEnumerable<T>` + `yield` on hot paths (allocates an enumerator); `foreach` over an interface-typed collection (boxes the enumerator); `async` methods that usually finish synchronously.
- **`params` arrays**, `Span` → `ToArray()` round trips, `MemoryStream.ToArray()` copies.
- **Per-record `byte[]` for JSON / `string` for XML:** can output share one large buffer with per-record slices (`ReadOnlyMemory<byte>`)? This is a 🔴 public-API change if it alters `EvtxEvent`/`EvtxChunk` shapes. Propose it, but flag it.
- `ValueStringBuilder` initial `stackalloc` sizes: if records routinely exceed them, every record rents from `ArrayPool` and probably grows it several times. Check the typical rendered size against the buffer.

Cost to name: "−N allocations per record (~B bytes)", "removes Gen1 promotion of X".

---

## Lens 3: Memory layout, copies and cache behaviour

You look for data movement: copies, oversized structs, pointer chasing and poor locality.

- **Large structs passed or returned by value** in hot calls (record structs like `EvtxRecord`, `EvtxChunkHeader`): use `in`/`ref readonly` parameters or return by `ref`. Check the struct size; above ~16–24 bytes, copies are noticeable.
- **Defensive copies:** calling non-`readonly` members on `readonly` fields or `in` parameters of non-readonly structs. Mark the struct or member `readonly`.
- **Class vs struct:** small, immutable, short-lived per-record objects as classes (heap and pointer chasing); or large structs held in `List<T>` and copied on every indexer access (use `CollectionsMarshal.AsSpan(list)` to iterate by ref).
- **Array of structs vs struct of arrays:** hot loops that touch one field of many objects; compiled template layouts where per-substitution metadata is spread across several objects.
- **Indirection:** `Dictionary<uint, X>` lookups where offsets are dense enough for a flat array; jagged arrays `T[][]` in inner loops; `IReadOnlyList<T>` hiding an array (interface dispatch on every index).
- **Span slicing copies:** `.ToArray()` on spans to call an API that takes arrays; `span[a..b]` creating many slices in a tight loop where an offset would do.
- **False sharing / contention:** per-thread counters or caches adjacent in memory and written by `Parallel.For` workers.
- **Buffer reuse:** fresh buffers per chunk where one per worker thread could be reused.

Cost to name: "removes N-byte struct copy per call", "removes pointer chase per substitution", "−1 interface dispatch per element".

---

## Lens 4: Branches, bounds checks and codegen

You look at what the JIT emits in 🔥 loops. Be sceptical: propose a change only when you're confident the JIT doesn't already handle it, or mark it ❓ and name the method whose disassembly would settle it (`bdn-run.ps1 … -Disasm`).

- **Bounds checks:** loops like `for (int i = 0; i < n; i++) span[i]` where `n` isn't `span.Length` (the JIT can't prove it's safe). Fix by slicing first: `span = span[..n]; for (i < span.Length)`. Also: indexing several arrays with one index, `span[pos + k]` reads with a moving `pos` (read once with `MemoryMarshal.Read<T>`/`BinaryPrimitives` from a pre-sliced span), and `(uint)i < (uint)len` tricks where the JIT misses the range.
- **Unaligned multi-byte reads:** byte-by-byte composition of u16/u32/u64 values instead of `BinaryPrimitives.ReadUInt32LittleEndian` / `MemoryMarshal.Read<T>`.
- **Dispatch:** virtual or interface calls in token loops; non-`sealed` classes with virtual members; generic methods over interfaces where a `struct` constraint would specialise the code.
- **Branchy decoding:** a large `switch` over a token or value type that doesn't compile to a jump table (sparse cases); `if` chains on a byte that a 256-entry lookup table (`ReadOnlySpan<byte> T => [...]`, which lives in the data section) would answer; per-character escaping decisions that a lookup table or `SearchValues<char>` would make branch-free.
- **Division and modulo** by non-constant values in hot loops; digit formatting doing `/10` and `%10` separately (use `Math.DivRem`, or two-digit tables).
- **Inlining:** small hot helpers that are too big or contain `throw` and so aren't inlined. Move the throw into a separate `[DoesNotReturn]` helper (`ThrowHelper` pattern) and consider `[MethodImpl(AggressiveInlining)]`. Conversely, flag `AggressiveInlining` on large methods (it bloats code and hurts the instruction cache).
- **Exceptions for control flow** on paths that malformed input hits often (exceptions are microseconds each).
- **Zero-init:** big `stackalloc` buffers that are always fully overwritten: `[SkipLocalsInit]` (🔴; it needs `AllowUnsafeBlocks`, and you must prove every byte is written before it's read).
- **Checked conversions** or `checked` arithmetic in loops; `long`/`int` conversion churn.

Cost to name: "removes bounds check in inner loop", "branch → table lookup", "interface call → direct call".

---

## Lens 5: Bulk operations, SIMD, I/O and encoding

You look for byte-at-a-time or char-at-a-time loops that the BCL can do in bulk, and for encoding round trips.

- **Search and scan:** manual loops searching for a byte or char → `IndexOf`, `IndexOfAny`, `IndexOfAnyExcept`, `IndexOfAnyInRange`, `SearchValues<T>` (create once as a `static readonly` field), `ContainsAny`. These are vectorised.
- **Compare and copy:** manual equality loops → `SequenceEqual`; manual copies → `CopyTo`/`Buffer.MemoryCopy`; manual zeroing → `Clear()`/`Fill()`.
- **Hashing and checksums:** byte-at-a-time table CRC32 (≈0.5 GB/s; `Crc32Chunk` in `FormatterBenchmarks` measures it). `System.IO.Hashing.Crc32` (NuGet, PCLMUL/ARM-accelerated) is often 10–20× faster. Adding a dependency is 🟡. Hand-rolled slice-by-8 or 16 is a dependency-free alternative.
- **Hex and number formatting:** per-nibble loops → `Convert.ToHexString` / `HexConverter`-style table writes into a span; `TryFormat` on integers directly into the destination span.
- **UTF-16 ↔ UTF-8:** BinXml strings are UTF-16LE. Look for decode-to-`string`-then-encode-to-UTF-8 on the JSON path (`Encoding.UTF8.GetBytes(string)`). Prefer `Utf8.FromUtf16(span, dest, …)` straight from the source bytes, `Utf8JsonWriter`/`IBufferWriter<byte>`, or writing escaped UTF-8 directly. Check whether the JSON path is ~3× slower than XML because of this (compare `ChunkBenchmarks` JsonWarm vs XmlWarm).
- **Escaping:** a fast path should handle a whole clean run with one vectorised `IndexOfAny`/`SearchValues` call and copy it in bulk, not test per character. Check that both the XML and JSON escapers do this, and that the JSON escaper's clean-text path costs about the same as the XML one (`FormatterBenchmarks` JsonEscapeClean vs XmlEscapeClean).
- **Explicit SIMD** (`Vector128/256<T>`, `Vector128.IsHardwareAccelerated` guards): only for a proven 🔥 loop that the BCL helpers can't express. Always keep a scalar fallback. This is 🟡 at least.
- **I/O:** small `Stream.Write` calls per record (batch into a buffer); `StreamWriter` with default buffer sizes on bulk output; `File.ReadAllBytes` where memory-mapping a huge file would avoid a copy (🔴, it changes the API).

Cost to name: "byte loop → vectorised IndexOfAny", "removes UTF-16→string→UTF-8 round trip per value", "N small writes → 1".
