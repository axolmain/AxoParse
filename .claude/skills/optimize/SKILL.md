---
name: optimize
description: Finds and applies measured speed/allocation improvements the way a competitive programmer would — hot paths first, algorithmic wins over micro-tweaks, every change proven with a BenchmarkDotNet before/after and reverted if it doesn't win. Use when the user asks to optimise, speed up, reduce allocations, find perf wins, or make a path faster.
allowed-tools:
  - AskUserQuestion
  - Agent
  - Bash
  - PowerShell
  - Edit
  - Write
  - Glob
  - Grep
  - Read
  - mcp__rider__dotTraceGetCallTree
  - mcp__rider__dotTraceGetSnapshotInfo
  - mcp__rider__dotTraceOpenReport
---

# Optimize

Make hot code faster or allocate less, without changing behaviour. **Back every claim with a measurement.**

## What optimisation is, and what it isn't

| In scope | Out of scope |
|---|---|
| Removing an O(n) factor (rescans, linear lookups, repeated work) | Making cold code (per-file, setup, error paths) harder to read for nanoseconds |
| Removing allocations on per-record / per-value paths | Style or naming changes, refactors for "cleanliness" (use `/simplify`) |
| Removing bounds checks, copies, boxing, virtual dispatch in loops | Bug hunting (use `/code-review`), except bugs your change would introduce |
| Vectorising scans (`IndexOfAny`, `SearchValues<T>`, `Vector128/256`) | Weakening validation of malformed EVTX input to gain speed |
| Better data layout (struct vs class, cache locality, lookup tables) | Speculative rewrites with no benchmark that can show the win |

**The test:** *Does this code run per record (or more often), and can I name the specific cost the change removes — an allocation, a copy, a branch, a bounds check, a dispatch, or an O(n) factor?* If not, skip it.

## Principles

### 1. Measure, don't guess
A proposal is a hypothesis until a benchmark confirms it. The JIT already inlines small methods, removes canonical bounds checks, devirtualises sealed types and unrolls some loops, so many "obvious" micro-optimisations do nothing. When unsure, check the disassembly (`-Disasm`) rather than reasoning about it.

### 2. Hotness decides everything
Classify code by how often it runs before judging it:

- 🔥 **Per byte / token / value**: BinXml token loop, value formatters, escaping, name lookups. Nanoseconds matter here.
- ♨️ **Per record**: record walk, template substitution, output materialisation (`string`/`byte[]` per record).
- 🧊 **Per chunk / file / setup**: chunk header, template preload, file header, `Parallel.For` setup, WEVT cache load.

Drop 🧊 proposals unless they're algorithmic (e.g. an O(n²) chunk scan) or remove contention that limits parallel scaling.

### 3. Algorithm first, then memory, then instructions
In order of typical payoff: (a) do less work (caching, early exit, no rescans), (b) allocate and copy less, (c) fewer branches, bounds checks and dispatches, (d) SIMD. Don't polish a loop that shouldn't exist.

### 4. Preserve behaviour exactly
Output must stay byte-for-byte identical (XML and JSON), as must error and diagnostic semantics, corruption tolerance and public API contracts. The test suite, including `ReferenceComparison` snapshots, is the oracle. A faster parser that drops a corrupted record it used to recover has regressed.

### 5. Respect the safety budget
- Use `unsafe`, `Unsafe.*`, `MemoryMarshal.GetReference` and `SkipLocalsInit` only when the safe form demonstrably can't match it. Each one is 🔴.
- Never remove a bounds/size check that guards against attacker-controlled lengths or offsets in EVTX data. Move it out of the loop (validate once, then index freely) instead.
- Watch `stackalloc` sizes: anything that depends on input sizes needs a cap and an `ArrayPool` fallback.

### 6. Follow project conventions
Read the repo's `CLAUDE.md`: explicit types (no `var`), XML docs on every member, no LINQ in hot paths, spans, `stackalloc`, structs. Optimised code still has to match the house style and document format-specific offsets.

### 7. Flag API boundaries
Changes to public signatures (e.g. returning `ReadOnlyMemory<byte>` instead of `string`, changing `IReadOnlyList<T>` to arrays) and changes to `EvtxEvent`/`EvtxRecord` shape are 🔴 and need explicit user approval. This is a NuGet library.

## Infrastructure

- **Benchmarks:** `perf/AxoParse.Benchmarks` (BenchmarkDotNet). See its `README.md`. Suites: `FileParseBenchmarks` (end to end), `ChunkBenchmarks` (Structure / XmlCold / XmlWarm / JsonCold / JsonWarm per chunk), `FormatterBenchmarks` (per-value micro). It can see `internal` members.
- **Run:** `pwsh tools/bench/bdn-run.ps1 <name> -Filter '<glob>' [-Job Short|Medium|Default] [-Disasm]` writes to `perf/results/<name>/`.
- **Compare:** `pwsh tools/bench/bdn-compare.ps1 perf/results/<a> perf/results/<b>` gives a FASTER/SLOWER/LESS-ALLOC/MORE-ALLOC/noise verdict per benchmark. A time change counts only if it is ≥3% and the 99.9% confidence intervals don't overlap. Exits with 1 on any regression.
- **Tests:** `dotnet test --project tests/AxoParse.Evtx.Tests`
- **Profiling (optional):** `perf/profiling` is a Stopwatch harness over `security_big_sample.evtx`. If the user has a dotTrace snapshot open in Rider, `mcp__rider__dotTraceGetCallTree` gives real hot-path data. Prefer it over call-site guessing.

## Instructions

### 1. Determine scope

- **Path or directory**: those files (recursively).
- **Git scope**: branch diff `git diff origin/master`, uncommitted `git diff HEAD`, unpushed `git diff origin/HEAD..HEAD`.
- **`hot`** (or "find the hot paths", "whole parser"): benchmark-driven. Start from step 2's numbers and pick the 2–4 most expensive areas, rather than starting from files.
- **No arguments**: ask with AskUserQuestion: **Hot paths (benchmark-driven)** (recommended) / **Branch diff** / **Uncommitted changes** / **Specific path** (Other).

If git reports "dubious ownership", pass `-c safe.directory='*'` to git commands rather than changing global config.

### 2. Map hotness and capture a baseline

1. For each file or method in scope, trace callers back to `EvtxParser.Parse` / `EvtxChunk.Parse` / `BinXmlParser` and tag it 🔥/♨️/🧊 (see Principle 2). Write this down as a short **hotness map**; agents receive it.
2. Pick the benchmark filter that covers the scope:
   - formatters, escaping, CRC → `*FormatterBenchmarks*`
   - BinXml parsing, template compile/substitution, record walk → `*ChunkBenchmarks*`
   - parallelism, end to end, caches shared across chunks → `*FileParseBenchmarks*` (narrow with a file, e.g. `*FileParseBenchmarks*security.evtx*`)
3. **If no benchmark covers hot code in scope, add one first** to `perf/AxoParse.Benchmarks`, following its README conventions and the project doc style. Without a benchmark there's nothing to verify against. Tell the user you added it.
4. Run the baseline **before touching any code**: `pwsh tools/bench/bdn-run.ps1 baseline -Filter '<glob>' -Job Medium`. Keep the summary table (mean, allocated, Gen0) for the agents and the final report.
5. Run the tests once to confirm they're green at baseline. If they aren't, stop and tell the user.

### 3. Analyse with lens agents (in parallel)

Launch **one `general-purpose` Agent per lens, all in a single message**, so they run concurrently. Lenses and their checklists are in [references/lenses.md](references/lenses.md). Give each agent:

- The **Principles** section above, verbatim.
- Its own lens section from `references/lenses.md`, verbatim. Read that file and paste the section in; don't just tell the agent the path.
- The scope (file list, or the diff), the hotness map and the baseline table.
- The path to the repo's `CLAUDE.md`, and the instruction to read related tests (`tests/AxoParse.Evtx.Tests/*`) to learn what behaviour must be preserved.
- This filter: *"Only propose a change on a 🔥 or ♨️ path (or an algorithmic/contention fix anywhere) where you can name the concrete cost removed. Don't propose things the JIT already does. If unsure whether the JIT handles it, mark the proposal ❓ and say which disassembly would settle it."*
- This output format, one block per proposal:
  ```
  ### <short title>
  - File: path:line
  - Lens: <lens>
  - Hotness: 🔥 | ♨️ | 🧊
  - Cost removed: <e.g. "1 string alloc per record (~40 B)", "O(n·m) → O(n)", "bounds check in inner loop">
  - Expected effect: <which benchmark(s) should move and roughly how much>
  - Risk: 🟢 | 🟡 | 🔴 | ❓ — <one-line reason>
  - Before: ```csharp ... ```
  - After:  ```csharp ... ```
  ```
- Agents must **not edit files**. They return proposals only.

Use all five lenses for a `hot` or whole-parser scope. For a narrow scope, launch only the lenses that apply (escaping code doesn't need the memory-layout lens).

### 4. Consolidate and present

- Merge duplicates. When two lenses found the same site, keep the better-justified proposal. Drop proposals that conflict with one already ranked higher.
- Rank by **expected impact = hotness × cost removed**. Algorithmic wins first.
- Present grouped by risk, then impact:
  - 🟢 **Mechanical**: local, clearly behaviour-preserving (hoisting, capacity hints, removing a copy, `SearchValues`).
  - 🟡 **Needs verification**: changes control flow, caching, data layout or readability noticeably.
  - 🔴 **Flag**: `unsafe`/`Unsafe.*`, removed validation, public API or threading changes.
  - ❓ **Unproven**: plausible, but the JIT may already do it, or the effect is unclear. Offer to check with `-Disasm` or a targeted micro-benchmark before applying.
- For each one show: number, title, hotness, cost removed, expected effect, and the before/after code. Keep the prose to one or two sentences; the code and the numbers carry the argument.

Then ask (AskUserQuestion): **Apply all 🟢** / **Apply specific numbers** / **Apply all except 🔴** / **Skip**.

### 5. Apply, then keep or revert based on measurement

Apply in impact order, **one proposal at a time** (or a tightly coupled group, which you must name). For each:

1. Before editing, note the exact current text of every region you'll change, so you can restore it. **Never use `git checkout`/`git restore`/`git stash` to revert**: the working tree may hold the user's uncommitted work.
2. Make the edit, matching house style (explicit types, XML docs, format-offset comments where they help).
3. `dotnet build perf/AxoParse.Benchmarks -c Release`. If it fails, fix once, otherwise revert and mark it ❌ build.
4. `dotnet test --project tests/AxoParse.Evtx.Tests`. **Any failure means revert**, mark it ❌ behaviour change and report the failing test. Don't "fix" tests to fit the optimisation.
5. `pwsh tools/bench/bdn-run.ps1 candidate -Filter '<same glob>' -Job Medium`, then `pwsh tools/bench/bdn-compare.ps1 perf/results/baseline perf/results/candidate`.
6. Decide:
   - **FASTER and/or LESS-ALLOC with no regression anywhere**: keep it. Copy `perf/results/candidate` over `perf/results/baseline` so the next proposal is measured against the new state.
   - **All noise**: revert, unless it's 🟢, removes allocations the benchmark doesn't cover, *and* is no less readable. Say which case applied.
   - **SLOWER or MORE-ALLOC anywhere**: revert, even if the target benchmark improved, and report the trade-off for the user to decide.
7. If the machine is noisy (big deltas inside overlapping confidence intervals), re-run once with `-Job Default` before deciding. Don't run more than twice; report it as inconclusive instead.

Don't commit. The user reviews the diff.

### 6. Report

Finish with a table like this one (the rows show the format; fill in real numbers from `bdn-compare`):

```
| # | Change | Hotness | Verdict | Time (target bench) | Alloc | Notes |
|---|--------|---------|---------|---------------------|-------|-------|
| 1 | <title> | 🔥 | ✅ kept | <base> → <new> (−x%) | <base> → <new> | |
| 2 | <title> | ♨️ | ↩️ reverted (noise) | <base> → <new> | <base> → <new> | alloc win, no time win; user's call |
| 3 | <title> | ♨️ | ❌ test failure | | | <failing test name> |
```

End with the cumulative before/after for the benchmarks that moved (baseline from step 2 vs. final state), and list the ❓ items that were left untested so the user can pick them up later.
