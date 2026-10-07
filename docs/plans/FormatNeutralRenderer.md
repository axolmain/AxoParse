# Plan: format-neutral renderer ("decode once, write many")

Branch: `features/format-neutral-renderer` (off `features/nuget` @ `0972526`).
Status: **plan only — no code changes yet.**

## Goal

Replace the per-format BinXml rendering code with one decoder, one format-neutral compiled template, and small
per-format writers. Two outcomes, in priority order:

1. **Faster template rendering.** The template loop and value dispatch are now the largest block of our own CPU time.
2. **Less code.** XML and JSON (and WEVT) each re-implement the same BinXml walking; bugs and optimisations have had
   to be done two or three times.

Hard constraint, unchanged from today: **output stays byte-identical** (XML and JSON, every sample, 1 and 8 threads),
and the test suite (including `ReferenceComparison`, which mirrors the Rust `evtx` project's `test_full_samples.rs`)
stays green. The Rust project's benchmark (hyperfine over the CLI on `security_big_sample.evtx`) must keep working
through `perf/AxoParse.Bench` with an unchanged command line.

## Where we are (features/nuget @ 0972526)

Hyperfine, AOT CLI, 30 MB `security_big_sample.evtx`, this Windows laptop (best clean runs):

| Case | Original | Now |
|---|---|---|
| XML, 1 thread | 267 ms | ~100 ms |
| XML, 8 threads | 175 ms | ~77 ms |
| JSON, 1 thread | 365 ms | ~108 ms |
| JSON, 8 threads | 207 ms | ~81 ms |

~50 ms of every run is process start/exit, incl. the CrowdStrike sensor (`csagent.sys`, ~13 ms/run) — not ours.

ETW profile, XML 1 thread, CPU ms per run (92 ms total; 44.5 ms in our code + runtime):

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

**Gates for every phase:** `dotnet test` green · in-process output hashes identical for all `tests/data` samples
(XML + JSON) · CLI output hashes identical for all 108 combos (samples × xml/json × 1/8 threads) · no hyperfine
regression beyond noise.

0. **Commit the safety net** (it currently lives only in a temp folder):
   - Golden-output test: SHA-256 per (sample, format) of concatenated `WriteTo` output, stored in the repo; a test
     regenerates and compares (with an env var to rebless intentionally).
   - `tools/bench/clihash.sh` — hash CLI stdout for every sample × format × 1/8 threads against a stored list.
   - `tools/bench/ab` — in-process A/B harness: loads two builds of `AxoParse.Evtx.dll` in separate
     `AssemblyLoadContext`s, alternates them every round on one pinned P-core with `DOTNET_TieredCompilation=0`,
     reports median ratio + p10..p90 + allocated KB. (A/A agrees within ~1.5%; BenchmarkDotNet runs on this laptop
     swing far more.)
   - `tools/bench/profile-cli.ps1` — elevated ETW capture (xperf kernel logger, PROFILE+CSWITCH stacks, large file
     buffers, lost-event check); analysis via `xperf -a profile -detail` with the AOT PDB on `_NT_SYMBOL_PATH`.
   - `tools/bench/clibench.sh` — publish AOT CLI (needs VS `vswhere` on PATH), verify hashes, hyperfine vs a baseline.
1. **Template program + compiler** (format-neutral) built alongside the existing compilers; a debug-only check that
   lowering it to XML/JSON reproduces today's compiled parts exactly for every template in the corpus.
2. **XML writer + renderer on the program** (compiled path); switch XML over; delete `BinXmlTemplateCompiler`,
   `CompiledTemplate`, XML `WriteCompiled`.
3. **JSON writer** incl. escape depth; switch JSON over; delete `BinXmlJsonTemplateCompiler`, `CompiledJsonTemplate`,
   JSON `WriteCompiledJson`.
4. **Fallback path** via decoder → writer; delete both hand-written fallback walkers.
5. **WEVT compiler onto the program** (and note: `WevtCache` is currently never read — see Out of scope).
6. **Instance execution**: merged descriptor read + one offset-keyed lookup.
7. **Tuning, each measured on its own**: per-gap type cache; GUID/SID/hex memo; integer formatting without the
   `NumberFormatInfo` lookup (`Utf8Formatter`); short-string escape-scan fast path; `SkipLocalsInit` (needs
   `AllowUnsafeBlocks` — ask first).

Expected: BinXml rendering code roughly halves (≈2,700 → ≈1,300 lines); render CPU down a few ms per run from phases
6–7; larger wins depend on how much of the 15.6 ms template loop the lowered program removes (measure, don't assume).

## Measurement notes (learned the hard way)

- This machine is noisy: a corporate agent bursts ~46% CPU; hybrid P/E cores. Pin to a P-core (logical 12, mask 4096),
  use the in-process A/B harness for code decisions, hyperfine for the end-to-end number.
- A freshly written executable can be slower or noisier for a while (security scanning); compare a fresh copy of the
  baseline binary too before calling a CLI regression.
- `pwsh` `> $null` decodes native output as text — always redirect to `NUL`/`-N` in hyperfine.
- Anything that touches the current culture loads ICU (~10 ms/run): keep the CLI and library culture-free.

## Out of scope here (separate decisions)

- **Lazy rendering + typed accessors** (EventID/Provider/Level/TimeCreated without rendering) — biggest win for
  filtering workloads; builds naturally on the decoder from this plan.
- **Shared cross-thread template cache** — also fixes `WevtCache` having no effect; changes recovered-chunk output, so
  needs an explicit decision.
- **8-thread path**: not yet profiled; system time exceeds user time — trace with `profile-cli.ps1 -Threads 8` first.
- **Unchecked sizes from file data**: `JsonEscapeString` (compile-time) is now bounded, but substitution counts > 64
  allocate heap arrays before validation, and a chunk header with Last < First record ID gives a negative list
  capacity — small fixes, do separately.
