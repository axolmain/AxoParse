# Plan: format-neutral renderer ("decode once, write many")

Branch: `features/format-neutral-renderer` (rebased onto `master` @ `a019756`, v0.2.1).
Status: **plan only — no code changes yet.** Revised 2026-10-18. Prerequisites shipped in v0.2.1 (#15): golden output
test, checksum/robustness fixes, pipelined `WriteTo`, corruption test. Streaming input (see Out of scope) is being done
first, also for 0.3.0.
Release: **0.3.0** (minor: adds `WriteTo(..., wevtCache)` and makes `WevtCache` do something).

## Goal

Replace the per-format BinXml rendering code with one decoder, one format-neutral compiled template, and small
per-format writers. Two outcomes, in priority order:

1. **Faster template rendering.** The template loop and value dispatch are now the largest block of our own CPU time.
2. **Less code.** XML and JSON (and WEVT) each re-implement the same BinXml walking; bugs and optimisations have had
   to be done two or three times.

Hard constraint, unchanged from today: **output stays byte-identical** (XML and JSON, every sample, 1 and 8 threads),
enforced by `GoldenOutputTests` and `AxoParse.CliHash`. The rest of the suite (including `ReferenceComparison`, which
mirrors the Rust `evtx` project's `test_full_samples.rs`, and `CorruptionFuzzTests`) stays green. The Rust project's
benchmark (hyperfine over the CLI on `security_big_sample.evtx`) must keep working through `perf/AxoParse.Bench` with
an unchanged command line. The one intended output change is phase 5's WEVT fallback. It only affects records whose
embedded template is missing or corrupt, and only when a `WevtCache` is passed.

## Where we are

Hyperfine, AOT CLI, 30 MB `security_big_sample.evtx`, 2025 MacBook Air (M4: 4 P + 6 E cores). These numbers include
the pipelined `WriteTo` and bench CLI changes from #15:

| Case | AxoParse | Rust `evtx` (same runs) |
|---|---|---|
| XML, 1 thread | 34.0 ms | 38.1 ms |
| JSON, 1 thread | 37.4 ms | 37.1 ms |
| XML, 8 threads | 12.9 ms | 9.4 ms |
| JSON, 8 threads | 14.1 ms | 8.7 ms |

Of the 1-thread time, ~3.5 ms is process start/exit and ~3.5 ms reading the file. In-process and warm, `WriteTo` takes
~29 ms on 1 thread and ~6 ms on 8. So this refactor mainly targets the ~29 ms of single-thread render CPU. The
remaining 8-thread gap is mostly the up-front file read and memory footprint (see "Streaming input" under Out of scope).

For reference, the original Windows laptop measured XML 1 thread 267 → ~100 ms, JSON 365 → ~108 ms at `0972526`. About
50 ms of every run there was process start/exit, including a corporate security agent.

ETW profile from that Windows laptop, XML 1 thread, CPU ms per run (92 ms total; 44.5 ms in our code + runtime). The
proportions should carry over, but re-profile on the Mac with `dotnet-trace` or Instruments before phase 6:

| ms | What |
|---|---|
| 8.5 + 7.1 | `BinXmlParser.WriteCompiled` + `WriteBinXmlValue` — template loop and per-value type switch |
| 3.4 | `SearchValues` escape scans (one vector search per string value) |
| 3.1 + 0.4 | `ReadTemplateInstanceData` (copies offsets/sizes/types into stack arrays) + zeroing them |
| 3.1 | per template instance: `Dictionary<uint, BinXmlTemplateDefinition>` + `Dictionary<Guid, CompiledTemplate>` lookups |
| 3.0 | `Memmove` (template parts, values) |
| ~2.8 | integer formatting (`TryUInt32ToDecStr` 1.3, `NumberFormatInfo.InvariantInfo` 0.9, `AppendFormatted` 0.6) |
| 2.9 | per-value UTF-16 → UTF-8 transcoding |
| 1.9 | FILETIME formatting |

## Current structure (what gets replaced)

| File | Lines | Role |
|---|---|---|
| `BinXml/BinXmlParser.cs` | 1045 | XML: token walk (fallback), template instances, value switch, compiled-template writer |
| `BinXml/BinXmlJsonWriter.cs` | 748 | JSON: the same, re-implemented, plus escaped (embedded BinXml-in-a-string) variants |
| `BinXml/BinXmlTemplateCompiler.cs` | 266 | XML template → string parts + slots |
| `BinXml/BinXmlJsonTemplateCompiler.cs` | 328 | JSON template → string parts + slots |
| `Wevt/WevtTemplateCompiler.cs` | 277 | WEVT (provider PE) template → XML parts |
| `BinXml/CompiledTemplate.cs`, `CompiledJsonTemplate.cs` | 238 | two compiled-template types |

Kept as-is: `ValueUtf8Builder` (UTF-8 output with exact surrogate semantics), `BinXmlValueFormatter` (UTF-8 value
formatters/escapers), `EvtxChunk`/`EvtxParser` (chunk pipeline, `WriteTo`, per-chunk UTF-8 storage).

## Target design

```
BinXml bytes ──► Decoder ──► (template instance) ──► TemplateProgram (cached per GUID) ──► Writer<TFormat> ──► ValueUtf8Builder
                       └──► (fallback tokens) ──────────────────────────────────────────► Writer<TFormat>
```

### 1. Format-neutral template program (the "instruction list")

Compile each template body once into a flat array of ops, independent of output format:

- `StartElement(name)`, `Attribute(name)` … `EndAttribute`, `CloseStartElement`, `EndElement(name)`, `EmptyElement`
- `Text(literal)` (already-unescaped text from Value tokens), `CharRef(n)`, `EntityRef(name)`, `CData(text)`
- `Substitution(subId, optional)` — the gap for a value
- Optional-attribute bracketing: an attribute whose only content is an optional substitution becomes one op
  (`OptionalAttribute(name, subId)`), so the writer decides "omit if empty" once instead of via prefix/suffix strings.

Format writers then *lower* the program once per (template, format) into the hot form: merged runs of pre-encoded
UTF-8 literal bytes + typed gap ops. This replaces today's `string[] Parts` + `SubSlot[]` and the three compilers.

### 2. Writer interface, specialised per format

```csharp
internal interface IBinXmlWriter
{
    static abstract void StartElement(ref ValueUtf8Builder b, ReadOnlySpan<char> name, ref WriterState s);
    static abstract void Attribute(ref ValueUtf8Builder b, ReadOnlySpan<char> name, ref WriterState s);
    // ... EndAttribute, CloseStart, EndElement, Text, CharRef, EntityRef, CData, Value(type, bytes) ...
}
// Renderer methods are generic: Render<TWriter>(...) where TWriter : struct, IBinXmlWriter
```

Static abstract members on a struct type parameter specialise and inline under both JIT and NativeAOT (no virtual
calls), so XML and JSON each get their own compiled copy of one renderer. The JSON-specific behaviours become writer
state, not special code paths:

| Today (special case) | In the writer |
|---|---|
| `{"#name":…,"#attrs":{…},"#content":[…]}` structure, comma tracking (`needsComma`) | JSON writer state machine |
| attribute values rendered as text, then JSON-escaped | JSON writer escapes `Text`/`Value` in attribute context |
| embedded BinXml (0x21) emitted as an escaped JSON string; `EscapedUtf8Parts`; `escaped` flags threaded through 6 methods | an escape-depth counter in writer state; lowering picks pre-escaped literals for depth ≥ 1 |
| XML entity resolution for JSON (`resolveEntities`) | JSON writer resolves `EntityRef`/`CharRef` |

Surrogate rules (the subtle part — see `ValueUtf8Builder` docs) carry over unchanged: escaped values use
`AppendUtf16Isolated`, names/CDATA/raw XML runs use `AppendUtf16`; literals containing surrogates are not pre-encoded.

### 3. Template instance execution (merges `ReadTemplateInstanceData` into the loop)

Read the substitution descriptor block (count, then `count × {u16 size, u8 type, u8 pad}`) and compute value offsets
with one prefix-sum pass into a pooled/stack buffer, then run the lowered program. Avoids copying three parallel arrays
and zeroing 576 bytes per instance. Look the program up **once per instance by chunk-relative template offset** (one
small per-chunk dictionary keyed by `defDataOffset`, falling back to the GUID cache on miss) instead of today's two
lookups.

### 4. Fallback (uncompilable templates, bare elements, recovered chunks)

The decoder walks tokens and drives the *same* writer directly (no compiled program). This replaces the two
hand-written fallback walkers (`ParseContent`/`ParseElement` and their JSON twins).

### 5. Value dispatch

Keep one `switch` on value type, but in one place (`WriteValue<TWriter>`), formats differing only in escaping and
quoting (writer callbacks). Phase 7 then tries per-gap type caching and a small memo for repeated GUID/SID/hex values.

## Phases (each ends with all gates green)

**Gates for every phase:** `dotnet test` green (incl. `GoldenOutputTests` and `CorruptionFuzzTests`) ·
`AxoParse.CliHash` passes all 104 AOT runs (samples × xml/json × 1/8 threads) · no regression beyond noise, measured
with `tools/bench/bdn-run.ps1` + `bdn-compare.ps1` (>3% and non-overlapping 99.9% CIs) for internals, and hyperfine
(alternating base/candidate/base) end to end.

0. **Safety net — done** (#15, v0.2.1). The original phase-0 scripts only ever existed in a temp folder on the Windows
   laptop and were rebuilt portably instead of recovered:
   - `GoldenOutputTests`: SHA-256 per (sample, format) of `WriteTo` output at 1 and 8 threads, re-bless with
     `AXOPARSE_REBLESS=1`, dumps mismatching output for diffing.
   - `perf/AxoParse.CliHash` (replaces `clihash.sh`): checks a built CLI against the same hashes. `bench-all.sh` runs
     it before timing.
   - `CorruptionFuzzTests`: seeded mutations, only `EvtxParseException` allowed (`AXOPARSE_FUZZ_ROUNDS` for long runs).
   - Not rebuilt: the in-process A/B harness, which existed because the Windows laptop was too noisy for
     BenchmarkDotNet. The M4 is quiet enough for BenchmarkDotNet. Rebuild it as a portable `dotnet run` tool only if
     noise blocks a decision. The ETW scripts are Windows-only. Profiling guidance: `dotnet-trace` + speedscope on any
     platform, Instruments on macOS, `perf` on Linux, WPR/xperf on Windows.
1. **Template program + compiler** (format-neutral) built alongside the existing compilers; a debug-only check that
   lowering it to XML/JSON reproduces today's compiled parts exactly for every template in the corpus.
2. **XML writer + renderer on the program** (compiled path); switch XML over; delete `BinXmlTemplateCompiler`,
   `CompiledTemplate`, XML `WriteCompiled`.
3. **JSON writer** incl. escape depth; switch JSON over; delete `BinXmlJsonTemplateCompiler`, `CompiledJsonTemplate`,
   JSON `WriteCompiledJson`.
4. **Fallback path** via decoder → writer; delete both hand-written fallback walkers.
5. **WEVT compiler onto the program, and `WevtCache` as a fallback.** Today `WevtCache` has no effect: its templates go
   into a dictionary the chunk workers never read. It must **not** be fixed by seeding the worker caches, because the
   cache is checked *before* a chunk's own template is compiled (so WEVT templates would override in-file ones), and
   today's `WevtTemplateCompiler` handles optional attributes differently (empty `attr=""` would appear). Match the
   Rust `evtx` crate instead (`src/binxml/ir.rs`, `get_or_parse_template_direct`): use a WEVT template **only when the
   chunk's embedded template is missing or fails to parse**, looked up by the GUID from the template instance header.
   That needs the lookup restructured so the early-return cases (`DataSize == 0`, body out of bounds) reach the
   fallback. With the format-neutral program, XML and JSON both get it. Add `wevtCache` to `WriteTo`. Port upstream's
   `test_wevt_cache_fallback.rs`: corrupt an embedded template, assert the record fails without the cache and renders
   with it, in both formats. Golden hashes stay unchanged: no sample has a missing template, and the golden test passes
   no cache.
6. **Instance execution**: merged descriptor read + one offset-keyed lookup.
7. **Tuning, each measured on its own**: per-gap type cache; GUID/SID/hex memo; integer formatting without the
   `NumberFormatInfo` lookup (`Utf8Formatter`); short-string escape-scan fast path; `SkipLocalsInit` (needs
   `AllowUnsafeBlocks` — ask first).

Expected: BinXml rendering code roughly halves (≈2,700 → ≈1,300 lines); render CPU down a few ms per run from phases
6–7; larger wins depend on how much of the 15.6 ms template loop the lowered program removes (measure, don't assume).

## Measurement notes (learned the hard way)

- **M4 (current machine):** 4 P + 6 E cores. Run-to-run variance at 8+ threads comes from P/E scheduling (the same
  build measured 6.5–10 ms in-process at 8 threads). Alternate base/candidate/base in one hyperfine call, use ≥ 40
  runs, and distrust a single ordering. A result inside ±2 ms at 8 threads needs a second ordered run.
- **On battery** the M4 doesn't throttle at these loads, but plug in for release numbers.
- **Windows laptop:** noisy (a corporate agent bursts ~46% CPU; hybrid P/E). Pin to a P-core (logical 12, mask 4096).
  A freshly written executable can be slower for a while (security scanning), so compare against a fresh copy of the
  baseline too. In `pwsh`, `> $null` decodes native output as text: always use `-N` in hyperfine.
- Anything that touches the current culture loads ICU (~10 ms/run on Windows): keep the library culture-free. The
  bench CLI now sets `InvariantGlobalization`.
- hyperfine 2.x needs `--shell=bash` (or `-N`) for commands with redirects, and its JSON export nests stats under
  `summary.time_wall_clock`.

## Out of scope here (separate decisions)

- **Lazy rendering + typed accessors** (EventID/Provider/Level/TimeCreated without rendering): the biggest win for
  filtering workloads, and it builds naturally on the decoder from this plan. Backlog issue, no milestone.
- **Shared cross-thread template cache**: measured as not worth it for speed (cold − warm is ~16 µs per chunk, ~0.1 ms
  wall at 8 threads). It no longer has anything to do with `WevtCache` (phase 5 makes that a fallback). Sharing across
  the recovery phase would change recovered-chunk output. Backlog issue, no milestone.
- **Streaming input** (`WriteTo(path or SafeFileHandle, …)`): the biggest remaining 8-thread lever. Workers would read
  their own 64 KB chunk (as `ParseStandalone` already does), overlapping the read with rendering and keeping memory
  flat. Rust peaks at 3–10 MB vs our 39–49 MB, because we hold the whole file. My estimate is ~10–11 ms at 8 threads.
  It's an API addition, so it ships in a minor release, and it's independent of this refactor. **In progress, ahead of
  this refactor (0.3.0).**
- ~~8-thread path~~: done in #15 (pipelined `WriteTo` plus bench CLI changes, −17%).
- ~~Unchecked sizes from file data~~: done in #15 (record ID range, substitution count, and the record size and
  template pointer/size overflows found by the corruption test).
