#!/usr/bin/env bash
# shellcheck shell=bash
# Unified benchmark function for XML and JSON formats.
# Expects all path variables, HAS_* flags, WARMUP, RUNS set by parent scope.
#
# Progress output goes to stderr (visible in real-time);
# only the final markdown table row is printed to stdout (captured by caller).
#
# Execution & display order: C# → Rust → JS → libevtx (C) → Go → pyevtx-rs

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

  # ── Go: velocidex (JSON only) ─────────────────────────────────────
  local idx_velocidex=-1
  if $HAS_VELOCIDEX && [[ "$fmt" == "json" ]]; then
    cmds+=(--command-name "velocidex" "'$EXTERNAL_DIR/velocidex-evtx/dumpevtx' parse '$file' > /dev/null")
    idx_velocidex=$next; ((next++))
  fi

  # ── Go: 0xrawsec (JSON only) ──────────────────────────────────────
  local idx_0xrawsec=-1
  if $HAS_0XRAWSEC && [[ "$fmt" == "json" ]]; then
    cmds+=(--command-name "0xrawsec" "'$EXTERNAL_DIR/0xrawsec-evtx/evtxdump' '$file' > /dev/null")
    idx_0xrawsec=$next; ((next++))
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
  $HAS_VELOCIDEX && append_result $idx_velocidex   "No support"
  $HAS_0XRAWSEC  && append_result $idx_0xrawsec    "No support"

  # pyevtx-rs (single run, not in hyperfine)
  if $HAS_PYEVTX_RS; then
    local pyrs_method="records"
    [[ "$fmt" == "json" ]] && pyrs_method="records_json"
    echo "    pyevtx-rs $fmt_upper (single run)..." >&2
    local pyrs_secs
    if pyrs_secs=$(measure_cmd_once_seconds "$(pyevtx_rs_cmd "$pyrs_method" "$file")" 2>/dev/null); then
      row="$row | $(format_single_run "$pyrs_secs")"
      echo "      $(format_single_run "$pyrs_secs")" >&2
    else
      row="$row | ERR"
    fi
  fi

  echo "$row |"
}
