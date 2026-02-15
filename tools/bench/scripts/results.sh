#!/usr/bin/env bash
# shellcheck shell=bash
# Markdown results output: table header, header metadata, results table, summary.
# Expects all path variables, HAS_* flags, NATIVE_ONLY, WARMUP, RUNS, RESULTS set by parent scope.

# ── List parsers ─────────────────────────────────────────────────────

# Print a single parser row for --list-parsers output.
print_parser_row() {
  local name="$1" has_flag="$2" bin_path="$3"
  local status="missing"
  if [[ "$has_flag" == "true" ]]; then
    status="available"
  elif [[ -n "${PARSER_FILTER:-}" ]] && ! parser_in_filter "$name"; then
    status="disabled"
  fi
  printf "%-22s %-12s %s\n" "$name" "$status" "$bin_path"
}

# Print parser availability table for --list-parsers.
list_parsers() {
  printf "%-22s %-12s %s\n" "Parser" "Status" "Path"
  printf "%-22s %-12s %s\n" "──────" "──────" "────"

  local rust_avail="false"
  [[ -x "$RUST_BIN" ]] && rust_avail="true"

  print_parser_row "rust"      "$rust_avail"     "$RUST_BIN"
  print_parser_row "csharp"    "$HAS_CSHARP"     "$CSHARP_BIN"
  print_parser_row "cs-wasm"   "$HAS_CS_WASM"    "$CS_WASM_CLI"
  print_parser_row "rust-wasm" "$HAS_RUST_WASM"  "$RUST_WASM_CLI"
  print_parser_row "js"        "$HAS_JS"         "$JS_CLI"
  print_parser_row "libevtx"   "$HAS_LIBEVTX"    "$EXTERNAL_DIR/libevtx/evtxtools/evtxexport"
  print_parser_row "velocidex" "$HAS_VELOCIDEX"  "$EXTERNAL_DIR/velocidex-evtx/dumpevtx"
  print_parser_row "0xrawsec"  "$HAS_0XRAWSEC"   "$EXTERNAL_DIR/0xrawsec-evtx/evtxdump"
  print_parser_row "pyevtx-rs" "$HAS_PYEVTX_RS"  "uv run --with evtx python"
}

# ── Table header ─────────────────────────────────────────────────────

# Append a column to the table header and separator.
add_column() {
  tbl_header="$tbl_header $1 |"
  tbl_sep="$tbl_sep----------------------|"
}

# Build the dynamic table header and separator based on active parsers.
# Sets: tbl_header, tbl_sep
build_table_header() {
  tbl_header="| File |"
  tbl_sep="|----------------------|"

  if $HAS_CSHARP; then
    add_column "C# (1 thread)"
    add_column "C# (8 threads)"
  fi
  $HAS_CS_WASM   && add_column "C# WASM"
  add_column "evtx (Rust - 1 thread)"
  add_column "evtx (Rust - 8 threads)"
  $HAS_RUST_WASM && add_column "Rust WASM"
  $HAS_JS        && add_column "JS Node"
  $HAS_LIBEVTX   && add_column "libevtx (C)"
  $HAS_VELOCIDEX && add_column "velocidex/evtx (Go)"
  $HAS_0XRAWSEC  && add_column "golang-evtx (Go)"
  $HAS_PYEVTX_RS && add_column "pyevtx-rs"
}

# ── Markdown output ──────────────────────────────────────────────────

# Write the metadata header block to $RESULTS (overwrites file).
write_markdown_header() {
  {
    echo "# Parser Benchmark Comparison"
    echo
    echo "| Field | Value |"
    echo "|-------|-------|"
    echo "| **Date** | $(date -u '+%Y-%m-%d %H:%M:%S UTC') |"
    if $HAS_JS; then echo "| **Node** | $(node --version) |"; fi
    echo "| **dotnet** | $(dotnet --version 2>/dev/null || echo 'N/A') |"
    echo "| **Platform** | $(uname -s) $(uname -m) |"
    echo "| **Rust binary** | \`evtx_dump --release\` |"
    if $NATIVE_ONLY; then echo "| **Mode** | native-only (C# + Rust) |"; fi
    echo "| **Warmup** | $WARMUP |"
    echo "| **Runs** | $RUNS |"
    if $HAS_LIBEVTX; then echo "| **libevtx (C)** | evtxexport (single-threaded) |"; fi
    if $HAS_VELOCIDEX; then echo "| **Velocidex (Go)** | dumpevtx |"; fi
    if $HAS_0XRAWSEC; then echo "| **0xrawsec (Go)** | evtxdump |"; fi
    if $HAS_PYEVTX_RS; then echo "| **pyevtx-rs** | via \`uv run --with evtx\` |"; fi
    echo
  } > "$RESULTS"
}

# Append the benchmark results table to $RESULTS.
# Usage: write_results_table "${ROWS[@]}"
write_results_table() {
  {
    echo "## Benchmark Results"
    echo
    echo "$tbl_header"
    echo "$tbl_sep"
    for row in "$@"; do
      echo "$row"
    done
    echo
    echo "**Note**: Numbers shown are \`real-time\` measurements (wall-clock time for invocation to complete). Single-run entries are marked with *(ran once)* — these parsers are too slow for repeated benchmarking via hyperfine."
    echo
  } >> "$RESULTS"
}

# Append the summary section to $RESULTS.
# Usage: write_summary <file_count> <pass_count> <fail_count>
write_summary() {
  local file_count="$1"
  local pass_count="$2"
  local fail_count="$3"

  {
    echo "## Summary"
    echo
    echo "- **Files tested:** $file_count"
    echo "- **Passed:** $pass_count"
    if $HAS_JS; then echo "- **Failed (JS):** $fail_count"; fi
    if $NATIVE_ONLY; then echo "- **Mode:** native-only (C# + Rust)"; fi
    echo
    echo "### Internal parsers"
    if $HAS_CSHARP; then echo "- C# native: yes"; fi
    echo "- Rust native: yes"
    if ! $NATIVE_ONLY; then
      if $HAS_JS; then echo "- JS Node: yes"; fi
      if $HAS_RUST_WASM; then echo "- Rust WASM: yes"; fi
      if $HAS_CS_WASM; then echo "- C# WASM (AOT): yes"; fi
      echo
      echo "### External parsers"
      if $HAS_LIBEVTX;   then echo "- libevtx (C): yes";   else echo "- libevtx (C): skipped";   fi
      if $HAS_VELOCIDEX;  then echo "- Velocidex (Go): yes"; else echo "- Velocidex (Go): skipped"; fi
      if $HAS_0XRAWSEC;   then echo "- 0xrawsec (Go): yes";  else echo "- 0xrawsec (Go): skipped";  fi
      if $HAS_PYEVTX_RS;  then echo "- pyevtx-rs: yes";      else echo "- pyevtx-rs: skipped";      fi
    fi
  } >> "$RESULTS"
}
