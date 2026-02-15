#!/usr/bin/env bash
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

# ── Helper functions ─────────────────────────────────────────────────

# Format hyperfine result as "275.9 ms +/- 2.1 ms" or "2.439 s +/- 0.035 s"
get_formatted() {
  node -e "
    const d = JSON.parse(require('fs').readFileSync('$1', 'utf8'));
    const r = d.results[$2];
    const mean = r.mean, sd = r.stddev;
    if (mean < 1.0) {
      console.log((mean*1000).toFixed(1) + ' ms ± ' + (sd*1000).toFixed(1) + ' ms');
    } else if (mean < 60.0) {
      console.log(mean.toFixed(3) + ' s ± ' + sd.toFixed(3) + ' s');
    } else {
      const m = Math.floor(mean / 60);
      const s = mean - m * 60;
      console.log(m + 'm' + s.toFixed(3) + 's ± ' + sd.toFixed(3) + ' s');
    }
  "
}

measure_cmd_once_seconds() {
  local cmd="$1"
  python3 - "$cmd" <<'PY'
import subprocess, sys, time
cmd = sys.argv[1]
start = time.perf_counter()
proc = subprocess.run(cmd, shell=True)
end = time.perf_counter()
if proc.returncode != 0:
    raise SystemExit(proc.returncode)
print(f"{end - start:.6f}")
PY
}

# Format single-run seconds as "0.367s (ran once)" or "2m41.075s (ran once)"
format_single_run() {
  python3 - "$1" <<'PY'
import sys
s = float(sys.argv[1])
if s >= 60.0:
    m = int(s // 60.0)
    rem = s - (m * 60.0)
    print(f"{m}m{rem:.3f}s (ran once)")
else:
    print(f"{s:.3f}s (ran once)")
PY
}

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
  rust, csharp, cs-wasm, rust-wasm, js, libevtx,
  velocidex, 0xrawsec, pyevtx-rs, python-evtx, python-evtx-pypy

Environment variables:
  HYPERFINE_WARMUP    Default warmup runs (overridden by --warmup)
  HYPERFINE_RUNS      Default benchmark runs (overridden by --runs)
  EXTERNAL_DIR        Directory for cloned external parsers
                      (default: $TMPDIR/evtx-bench)
  CS_WASM_FRAMEWORK   Path to C# WASM framework directory
USAGE
}

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
