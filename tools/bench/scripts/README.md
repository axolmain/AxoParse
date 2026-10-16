# Benchmark Scripts

Helper modules sourced by `../bench-all.sh`. Don't run these directly.

## Prerequisites

All parsers are built automatically on first run. You just need the toolchains installed.

**Required** (script exits without these):
- **hyperfine** — benchmark runner (1.x or 2.x)
- **Rust/cargo** — builds the Rust native parser
- **Node.js** — JS parser + WASM CLI wrappers
- **git** — clones external parser repos

**For full benchmarks** (gracefully skipped if missing):
- **.NET 10 SDK** — C# native parser
- **wasm-pack** plus the `wasm32-unknown-unknown` Rust target — Rust WASM build. Homebrew's `rust` formula doesn't
  ship that target; use rustup (`rustup target add wasm32-unknown-unknown`)
- **autoconf, automake, libtool, make** — building libevtx (C) from source

```bash
brew install hyperfine autoconf automake libtool wasm-pack
brew install node rustup dotnet
rustup target add wasm32-unknown-unknown
```

Use `--native-only` to skip everything except C# and Rust native benchmarks.

External parsers are cloned and built under `$EXTERNAL_DIR` (default `$TMPDIR/evtx-bench`).

Before timing anything, the C# CLI's output is checked against the golden hashes from `GoldenOutputTests`
(`perf/AxoParse.CliHash`: every sample × XML/JSON × 1/8 threads). If any output differs, the run stops instead of
benchmarking a broken build. Use `--skip-verify` to bypass this, for example while timing a deliberate output change
before re-blessing.

### Windows

These scripts are bash. Run them from WSL (recommended, since hyperfine, cargo and the autotools build of libevtx all
work there as on Linux) or Git Bash. Native Windows equivalents for the pieces that matter most:

- Output check: `dotnet run --project perf/AxoParse.CliHash -c Release -- perf\AxoParse.Bench\bin\publish\AxoParse.Bench.exe`
- Timing: `hyperfine -N "perf\AxoParse.Bench\bin\publish\AxoParse.Bench.exe tests\data\security_big_sample.evtx -t 1 -o xml"`.
  `-N` runs without a shell, so stdout goes to hyperfine's null sink. In PowerShell, don't redirect with `> $null`:
  it decodes native output as text and distorts the timing.

## Notes on the columns

- A cell shows `FAILED (exit N)` when any run of that command exited non-zero. `--ignore-failure` keeps hyperfine
  going, so without this check a crash would be reported as a fast time.
- **C# WASM** uses the paged API (`ParseEvtxFile` + `GetRecordPage`), as the web app does. `ParseEvtxToJson` builds a
  single string bigger than Node 26 will decode for the 30 MB sample.
- **Rust WASM** times upstream `evtx-wasm`'s Arrow IPC output (`chunk_arrow_ipc` per chunk). Upstream removed its JSON
  API, so this isn't comparable with the JSON columns.

| File | Purpose |
|------|---------|
| `lib.sh` | Colors, logging, timing helpers (`get_formatted`, etc.) |
| `builders.sh` | Clone + build functions for external parsers (Rust, libevtx, WASM) |
| `preflight.sh` | `run_preflight()` — tool checks, builds, sets `HAS_*` flags |
| `run-benchmarks.sh` | `run_benchmark <xml\|json>` — builds the hyperfine command list and the markdown row |
| `results.sh` | Markdown output: table header, metadata, results table, summary |

## Adding a new parser

1. Add a build function in `builders.sh`
2. Add a `HAS_*` flag in `preflight.sh` and call the builder
3. Add the hyperfine command in `run-benchmarks.sh` (XML, JSON, or both)
4. Add the column in `results.sh` (`build_table_header`) and `write_summary`
