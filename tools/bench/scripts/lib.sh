#!/usr/bin/env bash
# shellcheck shell=bash
# Color helpers and utility functions for benchmarking.

# ── Color helpers ────────────────────────────────────────────────────
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

log_info()    { echo -e "${BLUE}[INFO]${NC} $*"; }
log_success() { echo -e "${GREEN}[OK]${NC} $*"; }
log_warn()    { echo -e "${YELLOW}[WARN]${NC} $*"; }
log_error()   { echo -e "${RED}[ERROR]${NC} $*"; }

# ── Cross-platform helpers ───────────────────────────────────────────

# Available CPU count (Linux nproc, macOS sysctl, fallback 4).
ncpus() { nproc 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null || echo 4; }

# ── Benchmark helpers ────────────────────────────────────────────────

# Format hyperfine result as "275.9 ms ± 2.1 ms" or "2.439 s ± 0.035 s".
get_formatted() {
  node -e "
    const d = JSON.parse(require('fs').readFileSync(process.argv[1], 'utf8'));
    const r = d.results[parseInt(process.argv[2])];
    if (!r || r.mean == null) { console.log('ERR'); process.exit(0); }
    const mean = r.mean, sd = r.stddev;
    if (mean < 1.0) {
      console.log((mean*1000).toFixed(1) + ' ms \u00b1 ' + (sd*1000).toFixed(1) + ' ms');
    } else if (mean < 60.0) {
      console.log(mean.toFixed(3) + ' s \u00b1 ' + sd.toFixed(3) + ' s');
    } else {
      const m = Math.floor(mean / 60);
      const s = mean - m * 60;
      console.log(m + 'm' + s.toFixed(3) + 's \u00b1 ' + sd.toFixed(3) + ' s');
    }
  " "$1" "$2"
}

# Check if a parser name is in the PARSER_FILTER comma-separated list.
parser_in_filter() {
  local name="$1" p
  IFS=',' read -ra _pf <<< "$PARSER_FILTER"
  for p in "${_pf[@]}"; do
    [[ "$p" == "$name" ]] && return 0
  done
  return 1
}

# ── CLI help ─────────────────────────────────────────────────────────

show_help() {
  cat <<'USAGE'
Usage: bench-all.sh [OPTIONS]

Options:
  --help              Show this help message and exit
  --file <path>       Benchmark a single .evtx file instead of scanning TEST_DATA
  --json-only         Run only JSON benchmarks
  --xml-only          Run only XML benchmarks
  --runs N            Number of hyperfine runs (default: $HYPERFINE_RUNS or 10)
  --warmup N          Number of hyperfine warmup runs (default: $HYPERFINE_WARMUP or 5)
  --no-build          Skip all build steps; use existing binaries
  --native-only       Run only native parsers (C# + Rust), skip WASM/JS/external
  --parsers a,b,c     Run only the listed parsers (comma-separated)
  --list-parsers      Show available parsers and their status, then exit

Parser names for --parsers:
  rust, csharp, cs-wasm, rust-wasm, js,
  libevtx

Environment variables:
  HYPERFINE_WARMUP    Default warmup runs (overridden by --warmup)
  HYPERFINE_RUNS      Default benchmark runs (overridden by --runs)
  EXTERNAL_DIR        Directory for cloned external parsers
                      (default: $TMPDIR/evtx-bench)
  CS_WASM_FRAMEWORK   Path to C# WASM framework directory
USAGE
}

# ── Binary compatibility ─────────────────────────────────────────────

# Check if a compiled binary matches the current OS (prevents running
# Linux binaries on macOS and vice versa). Returns 0 (compatible) when
# the `file` command is unavailable — permissive fallback since we
# cannot determine compatibility without it.
binary_looks_compatible() {
  local bin="$1"
  [[ -f "$bin" ]] || return 1

  if ! command -v file >/dev/null 2>&1; then
    return 0
  fi

  local os desc
  os="$(uname -s 2>/dev/null || echo unknown)"
  desc="$(file "$bin" 2>/dev/null || true)"

  case "$os" in
    Linux)  [[ "$desc" == *"Mach-O"* ]] && return 1 ;;
    Darwin) [[ "$desc" == *"ELF"* ]]    && return 1 ;;
  esac

  return 0
}
