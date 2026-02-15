#!/usr/bin/env bash
set -euo pipefail

# ── Default flag values ──────────────────────────────────────────────
NATIVE_ONLY=false
NO_BUILD=false
FORMAT_FILTER=""
SINGLE_FILE=""
PARSER_FILTER=""
LIST_PARSERS=false

# ── Path variables (set BEFORE sourcing helpers) ────────────────────
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
CSHARP_PROJECT="$ROOT_DIR/src/AxoParse.Bench/AxoParse.Bench.csproj"
CSHARP_BIN="$ROOT_DIR/src/AxoParse.Bench/bin/publish/AxoParse.Bench"
CS_WASM_CLI="$SCRIPT_DIR/bench-wasm-cli.mjs"
CS_WASM_FRAMEWORK="${CS_WASM_FRAMEWORK:-}"
RUST_WASM_CLI="$SCRIPT_DIR/bench-rust-wasm-cli.mjs"

# External parsers directory (defaults to system temp dir)
EXTERNAL_DIR="${EXTERNAL_DIR:-${TMPDIR:-/tmp}/evtx-bench}"

# Rust evtx parser (cloned from GitHub into external dir)
RUST_DIR="$EXTERNAL_DIR/evtx-rust"
RUST_BIN="$RUST_DIR/target/release/evtx_dump"
RUST_WASM_PKG="$RUST_DIR/evtx-wasm/pkg"

# JS evtx parser (cloned from axolmain/evtx-parser into external dir)
JS_REPO_DIR="$EXTERNAL_DIR/axolmain-evtx-parser"
JS_CLI="$JS_REPO_DIR/tests/bench/bench-cli.mjs"

# Hyperfine settings (env-overridable, CLI flags override below)
WARMUP="${HYPERFINE_WARMUP:-5}"
RUNS="${HYPERFINE_RUNS:-10}"

# Test data
TEST_DATA="$ROOT_DIR/tests/data/benchmark"

# Output file
RESULTS="$SCRIPT_DIR/benchmark-comparison.md"

# ── Source helpers ──────────────────────────────────────────────────
source "$SCRIPT_DIR/scripts/lib.sh"
source "$SCRIPT_DIR/scripts/builders.sh"
source "$SCRIPT_DIR/scripts/preflight.sh"
source "$SCRIPT_DIR/scripts/run-benchmarks.sh"
source "$SCRIPT_DIR/scripts/results.sh"

# ── Parse flags (while/case/shift) ─────────────────────────────────
while [[ $# -gt 0 ]]; do
  case "$1" in
    --help)
      show_help
      exit 0
      ;;
    --file)
      SINGLE_FILE="$2"
      shift 2
      ;;
    --json-only)
      FORMAT_FILTER="json"
      shift
      ;;
    --xml-only)
      FORMAT_FILTER="xml"
      shift
      ;;
    --runs)
      RUNS="$2"
      shift 2
      ;;
    --warmup)
      WARMUP="$2"
      shift 2
      ;;
    --no-build)
      NO_BUILD=true
      shift
      ;;
    --native-only)
      NATIVE_ONLY=true
      shift
      ;;
    --parsers)
      PARSER_FILTER="$2"
      shift 2
      ;;
    --list-parsers)
      LIST_PARSERS=true
      shift
      ;;
    *)
      log_error "Unknown option: $1"
      show_help
      exit 1
      ;;
  esac
done

# ── Preflight (sets HAS_* variables) ───────────────────────────────
run_preflight

# ── List parsers and exit if requested ─────────────────────────────
if $LIST_PARSERS; then
  list_parsers
  exit 0
fi

# ── Collect .evtx files ──────────────────────────────────────────────
declare -a FILES=()

if [[ -n "$SINGLE_FILE" ]]; then
  if [[ ! -f "$SINGLE_FILE" ]]; then
    log_error "File not found: $SINGLE_FILE"
    exit 1
  fi
  FILES=("$SINGLE_FILE")
elif [[ -d "$TEST_DATA" ]]; then
  while IFS= read -r -d '' f; do
    FILES+=("$f")
  done < <(find "$TEST_DATA" -name '*.evtx' -print0 | sort -z)
fi

if [[ ${#FILES[@]} -eq 0 ]]; then
  echo "No .evtx files found in $TEST_DATA" >&2
  exit 1
fi

echo "Found ${#FILES[@]} .evtx files"
echo ""

# ── Build table header + write markdown header ─────────────────────
build_table_header
write_markdown_header

# ── Storage for rows ────────────────────────────────────────────────
declare -a ROWS=()

# ── Run benchmarks ──────────────────────────────────────────────────
i=0
pass=0
fail=0

for file in "${FILES[@]}"; do
  i=$((i + 1))
  name="$(basename "$file")"
  size="$(du -h "$file" | cut -f1 | xargs)"

  echo "[$i/${#FILES[@]}] $name ($size)"

  # Run JS parser first to check it doesn't error out (skip in native-only mode)
  if $HAS_JS; then
    if ! node "$JS_CLI" "$file" -o json &>/dev/null; then
      log_warn "JS parser failed — skipping"
      fail=$((fail + 1))
      continue
    fi
  fi

  file_label="$size $name"

  # XML benchmark (skipped if --json-only)
  if [[ "$FORMAT_FILTER" != "json" ]]; then
    ROWS+=("$(run_xml_benchmark "$file" "$file_label")")
  fi

  # JSON benchmark (skipped if --xml-only)
  if [[ "$FORMAT_FILTER" != "xml" ]]; then
    ROWS+=("$(run_json_benchmark "$file" "$file_label")")
  fi

  pass=$((pass + 1))
  echo ""
done

# ── Write results + summary ─────────────────────────────────────────
write_results_table "${ROWS[@]}"
write_summary "${#FILES[@]}" "$pass" "$fail"

echo "========================================="
echo "Done! $pass passed, $fail failed"
echo "Results written to: $RESULTS"
echo "========================================="
