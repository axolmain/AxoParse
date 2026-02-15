# Benchmark Scripts

Helper modules sourced by `../bench-all.sh`. Don't run these directly.

## Prerequisites

All parsers are built automatically on first run. You just need the toolchains installed.

**Required** (script exits without these):
- **hyperfine** — benchmark runner
- **Rust/cargo** — builds the Rust native parser
- **Node.js** — JS parser + WASM CLI wrappers
- **git** — clones external parser repos

**For full benchmarks** (gracefully skipped if missing):
- **.NET 10 SDK** — C# native parser
- **Go** — Velocidex + 0xrawsec parsers
- **wasm-pack** — Rust WASM build
- **Python 3 + uv** — python-evtx + pyevtx-rs
- **autoconf, automake, libtool, make** — building libevtx (C) from source

```bash
brew install hyperfine autoconf automake libtool
brew install node rustup go dotnet uv python@3
cargo install wasm-pack
```

Use `--native-only` to skip everything except C# and Rust native benchmarks.

| File | Purpose |
|------|---------|
| `lib.sh` | Colors, logging, timing helpers (`get_formatted`, `measure_cmd_once_seconds`, etc.) |
| `builders.sh` | Clone + build functions for external parsers (Rust, libevtx, Go, Python, WASM) |
| `preflight.sh` | `run_preflight()` — tool checks, builds, sets `HAS_*` flags |
| `run-benchmarks.sh` | `run_xml_benchmark` / `run_json_benchmark` — hyperfine invocation + single-run fallbacks |
| `results.sh` | Markdown output: table header, metadata, results table, summary |

## Adding a new parser

1. Add a build function in `builders.sh`
2. Add a `HAS_*` flag in `preflight.sh` and call the builder
3. Add the hyperfine command in `run-benchmarks.sh` (XML, JSON, or both)
4. Add the column in `results.sh` (`build_table_header`) and `write_summary`
