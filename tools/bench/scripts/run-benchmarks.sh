#!/usr/bin/env bash
# shellcheck shell=bash
# Unified benchmark function for XML and JSON formats.
# Expects all path variables, HAS_* flags, WARMUP, RUNS set by parent scope.
#
# Progress output goes to stderr (visible in real-time);
# only the final markdown table row is printed to stdout (captured by caller).
#
# Execution & display order: C# → Rust → JS → libevtx (C)

# Run benchmarks for a single .evtx file in the specified format.
# Usage: run_benchmark <format> <file> <file_label>
# format: "xml" or "json"
run_benchmark() {
  local fmt="$1"
  local file="$2"
  local file_label="$3"
  local fmt_upper
  fmt_upper=$(echo "$fmt" | tr '[:lower:]' '[:upper:]')

  echo "  $fmt_upper benchmark..." >&2
  local tmp_file
  tmp_file=$(mktemp)
  trap 'rm -f "$tmp_file"' RETURN

  declare -a cmds=()
  local next=0

  # ── C# native ──────────────────────────────────────────────────────
  local idx_cs1t=-1 idx_cs8t=-1
  if $HAS_CSHARP; then
    cmds+=(--command-name "csharp-1t" "'$CSHARP_BIN' '$file' -t 1 -o $fmt > /dev/null")
    cmds+=(--command-name "csharp-8t" "'$CSHARP_BIN' '$file' -t 8 -o $fmt > /dev/null")
    idx_cs1t=$next; ((next++))
    idx_cs8t=$next; ((next++))
  fi

  # ── C# WASM (JSON only) ───────────────────────────────────────────
  local idx_cs_wasm=-1
  if $HAS_CS_WASM && [[ "$fmt" == "json" ]]; then
    cmds+=(--command-name "csharp-wasm" "CS_WASM_FRAMEWORK='$CS_WASM_FRAMEWORK' node '$CS_WASM_CLI' '$file' > /dev/null")
    idx_cs_wasm=$next; ((next++))
  fi

  # ── Rust native ────────────────────────────────────────────────────
  local idx_rust1t idx_rust8t
  local rust_fmt=""
  [[ "$fmt" == "json" ]] && rust_fmt="-o json "
  cmds+=(--command-name "rust-1t" "'$RUST_BIN' -t 1 ${rust_fmt}'$file' > /dev/null")
  cmds+=(--command-name "rust-8t" "'$RUST_BIN' -t 8 ${rust_fmt}'$file' > /dev/null")
  idx_rust1t=$next; ((next++))
  idx_rust8t=$next; ((next++))

  # ── Rust WASM (JSON only) ─────────────────────────────────────────
  local idx_rust_wasm=-1
  if $HAS_RUST_WASM && [[ "$fmt" == "json" ]]; then
    cmds+=(--command-name "rust-wasm" "EXTERNAL_DIR='$EXTERNAL_DIR' node '$RUST_WASM_CLI' '$file' > /dev/null")
    idx_rust_wasm=$next; ((next++))
  fi

  # ── JS Node ────────────────────────────────────────────────────────
  local idx_js=-1
  if $HAS_JS; then
    cmds+=(--command-name "js-node" "node '$JS_CLI' '$file' -o $fmt > /dev/null")
    idx_js=$next; ((next++))
  fi

  # ── libevtx (XML only) ────────────────────────────────────────────
  local idx_libevtx=-1
  if $HAS_LIBEVTX && [[ "$fmt" == "xml" ]]; then
    cmds+=(--command-name "libevtx" "'$EXTERNAL_DIR/libevtx/evtxtools/evtxexport' -f xml '$file' > /dev/null")
    idx_libevtx=$next; ((next++))
  fi

  hyperfine --warmup "$WARMUP" --runs "$RUNS" --style basic \
    --ignore-failure \
    --export-json "$tmp_file" \
    "${cmds[@]}" \
    2>&1 | sed 's/^/    /' >&2

  # ── Build table row ────────────────────────────────────────────────
  local row="| $file_label ($fmt_upper)"

  # Helper: append formatted result or a static label
  append_result() {
    local idx="$1"
    if [[ $idx -ge 0 ]]; then
      row="$row | $(get_formatted "$tmp_file" "$idx")"
    else
      row="$row | $2"
    fi
  }

  if $HAS_CSHARP; then
    append_result $idx_cs1t
    append_result $idx_cs8t
  fi
  $HAS_CS_WASM   && append_result $idx_cs_wasm   "N/A (web only)"
  append_result $idx_rust1t
  append_result $idx_rust8t
  $HAS_RUST_WASM && append_result $idx_rust_wasm  "N/A (web only)"
  $HAS_JS        && append_result $idx_js
  $HAS_LIBEVTX   && append_result $idx_libevtx    "No support"

  echo "$row |"
}
