#!/usr/bin/env bash
# XML and JSON benchmark functions.
# Expects all path variables, HAS_* flags, WARMUP, RUNS set by parent scope.
#
# Both functions send progress output to stderr (visible in real-time)
# and print only the final markdown table row to stdout (captured by caller).

# Run XML benchmarks for a single .evtx file.
# Usage: run_xml_benchmark <file> <file_label>
# Prints the markdown table row to stdout.
run_xml_benchmark() {
  local file="$1"
  local file_label="$2"

  echo "  XML benchmark..." >&2
  local xml_tmp
  xml_tmp=$(mktemp)
  declare -a xml_cmds=(
    --command-name "rust-1t" "'$RUST_BIN' -t 1 '$file' > /dev/null"
    --command-name "rust-8t" "'$RUST_BIN' -t 8 '$file' > /dev/null"
  )
  local xml_idx_rust1t=0
  local xml_idx_rust8t=1
  local xml_next=2

  local xml_idx_js=-1
  if $HAS_JS; then
    xml_cmds+=(--command-name "js-node" "node '$JS_CLI' '$file' -o xml > /dev/null")
    xml_idx_js=$xml_next; xml_next=$((xml_next + 1))
  fi

  local xml_idx_cs1t=-1 xml_idx_cs8t=-1 xml_idx_libevtx=-1

  if $HAS_CSHARP; then
    xml_cmds+=(--command-name "csharp-1t" "'$CSHARP_BIN' '$file' -t 1 -o xml > /dev/null")
    xml_cmds+=(--command-name "csharp-8t" "'$CSHARP_BIN' '$file' -t 8 -o xml > /dev/null")
    xml_idx_cs1t=$xml_next; xml_next=$((xml_next + 1))
    xml_idx_cs8t=$xml_next; xml_next=$((xml_next + 1))
  fi

  if $HAS_LIBEVTX; then
    xml_cmds+=(--command-name "libevtx" "'$EXTERNAL_DIR/libevtx/evtxtools/evtxexport' -f xml '$file' > /dev/null")
    xml_idx_libevtx=$xml_next; xml_next=$((xml_next + 1))
  fi

  hyperfine --warmup "$WARMUP" --runs "$RUNS" --style basic \
    --export-json "$xml_tmp" \
    "${xml_cmds[@]}" \
    2>&1 | sed 's/^/    /' >&2

  local xml_row="| $file_label (XML)"

  # C# native
  if $HAS_CSHARP; then
    xml_row="$xml_row | $(get_formatted "$xml_tmp" $xml_idx_cs1t) | $(get_formatted "$xml_tmp" $xml_idx_cs8t)"
  fi
  # C# WASM — XML not ran (web-only)
  if $HAS_CS_WASM; then
    xml_row="$xml_row | not ran bc it's for web"
  fi
  # Rust native
  xml_row="$xml_row | $(get_formatted "$xml_tmp" $xml_idx_rust1t) | $(get_formatted "$xml_tmp" $xml_idx_rust8t)"
  # Rust WASM — XML not ran (web-only)
  if $HAS_RUST_WASM; then
    xml_row="$xml_row | not ran bc it's for web"
  fi
  # JS Node
  if $HAS_JS; then
    xml_row="$xml_row | $(get_formatted "$xml_tmp" $xml_idx_js)"
  fi
  # libevtx (C)
  if $HAS_LIBEVTX; then
    xml_row="$xml_row | $(get_formatted "$xml_tmp" $xml_idx_libevtx)"
  fi
  # velocidex — XML not supported
  if $HAS_VELOCIDEX; then
    xml_row="$xml_row | No support"
  fi
  # 0xrawsec — XML not supported
  if $HAS_0XRAWSEC; then
    xml_row="$xml_row | No support"
  fi
  # pyevtx-rs (single run)
  if $HAS_PYEVTX_RS; then
    echo "    pyevtx-rs XML (single run)..." >&2
    local pyrs_xml_secs
    if pyrs_xml_secs=$(measure_cmd_once_seconds "uv run --with evtx python -c 'import sys, collections; from evtx import PyEvtxParser; p=PyEvtxParser(sys.argv[1]); collections.deque((sys.stdout.write(r[\"data\"] + \"\\n\") for r in p.records()), maxlen=0)' '$file' > /dev/null" 2>/dev/null); then
      xml_row="$xml_row | $(format_single_run "$pyrs_xml_secs")"
      echo "      $(format_single_run "$pyrs_xml_secs")" >&2
    else
      xml_row="$xml_row | ERR"
    fi
  fi

  # python-evtx CPython (single run, too slow for hyperfine)
  if $HAS_PYTHON_EVTX; then
    echo "    python-evtx CPython (single run)..." >&2
    local local_python="$EXTERNAL_DIR/python-evtx/.venv-cpython/bin/python"
    local local_script="$EXTERNAL_DIR/python-evtx/evtx_scripts/evtx_dump.py"
    local py_secs
    if py_secs=$(measure_cmd_once_seconds "PYTHON_JIT=1 '$local_python' '$local_script' '$file' > /dev/null" 2>/dev/null); then
      xml_row="$xml_row | $(format_single_run "$py_secs")"
      echo "      $(format_single_run "$py_secs")" >&2
    else
      xml_row="$xml_row | ERR"
    fi
  fi

  # python-evtx PyPy (single run)
  if $HAS_PYTHON_EVTX_PYPY; then
    echo "    python-evtx PyPy (single run)..." >&2
    local local_pypy="$EXTERNAL_DIR/python-evtx/.venv-pypy/bin/python"
    local local_script="$EXTERNAL_DIR/python-evtx/evtx_scripts/evtx_dump.py"
    local pypy_secs
    if pypy_secs=$(measure_cmd_once_seconds "'$local_pypy' '$local_script' '$file' > /dev/null" 2>/dev/null); then
      xml_row="$xml_row | $(format_single_run "$pypy_secs")"
      echo "      $(format_single_run "$pypy_secs")" >&2
    else
      xml_row="$xml_row | ERR"
    fi
  fi

  echo "$xml_row |"

  rm -f "$xml_tmp"
}

# Run JSON benchmarks for a single .evtx file.
# Usage: run_json_benchmark <file> <file_label>
# Prints the markdown table row to stdout.
run_json_benchmark() {
  local file="$1"
  local file_label="$2"

  echo "  JSON benchmark..." >&2
  local json_tmp
  json_tmp=$(mktemp)
  declare -a json_cmds=(
    --command-name "rust-1t" "'$RUST_BIN' -t 1 -o json '$file' > /dev/null"
    --command-name "rust-8t" "'$RUST_BIN' -t 8 -o json '$file' > /dev/null"
  )
  local json_idx_rust1t=0
  local json_idx_rust8t=1
  local json_next=2

  local json_idx_js=-1
  if $HAS_JS; then
    json_cmds+=(--command-name "js-node" "node '$JS_CLI' '$file' -o json > /dev/null")
    json_idx_js=$json_next; json_next=$((json_next + 1))
  fi

  local json_idx_cs1t=-1 json_idx_cs8t=-1 json_idx_rust_wasm=-1 json_idx_cs_wasm=-1
  local json_idx_velocidex=-1 json_idx_0xrawsec=-1

  if $HAS_CSHARP; then
    json_cmds+=(--command-name "csharp-1t" "'$CSHARP_BIN' '$file' -t 1 -o json > /dev/null")
    json_cmds+=(--command-name "csharp-8t" "'$CSHARP_BIN' '$file' -t 8 -o json > /dev/null")
    json_idx_cs1t=$json_next; json_next=$((json_next + 1))
    json_idx_cs8t=$json_next; json_next=$((json_next + 1))
  fi

  if $HAS_CS_WASM; then
    json_cmds+=(--command-name "csharp-wasm" "node '$CS_WASM_CLI' '$file' > /dev/null")
    json_idx_cs_wasm=$json_next; json_next=$((json_next + 1))
  fi

  if $HAS_RUST_WASM; then
    json_cmds+=(--command-name "rust-wasm" "EXTERNAL_DIR='$EXTERNAL_DIR' node '$RUST_WASM_CLI' '$file' > /dev/null")
    json_idx_rust_wasm=$json_next; json_next=$((json_next + 1))
  fi

  if $HAS_VELOCIDEX; then
    json_cmds+=(--command-name "velocidex" "'$EXTERNAL_DIR/velocidex-evtx/dumpevtx' parse '$file' > /dev/null")
    json_idx_velocidex=$json_next; json_next=$((json_next + 1))
  fi

  if $HAS_0XRAWSEC; then
    json_cmds+=(--command-name "0xrawsec" "'$EXTERNAL_DIR/0xrawsec-evtx/evtxdump' '$file' > /dev/null")
    json_idx_0xrawsec=$json_next; json_next=$((json_next + 1))
  fi

  hyperfine --warmup "$WARMUP" --runs "$RUNS" --style basic \
    --export-json "$json_tmp" \
    "${json_cmds[@]}" \
    2>&1 | sed 's/^/    /' >&2

  local json_row="| $file_label (JSON)"

  # C# native
  if $HAS_CSHARP; then
    json_row="$json_row | $(get_formatted "$json_tmp" $json_idx_cs1t) | $(get_formatted "$json_tmp" $json_idx_cs8t)"
  fi
  # C# WASM
  if $HAS_CS_WASM; then
    json_row="$json_row | $(get_formatted "$json_tmp" $json_idx_cs_wasm)"
  fi
  # Rust native
  json_row="$json_row | $(get_formatted "$json_tmp" $json_idx_rust1t) | $(get_formatted "$json_tmp" $json_idx_rust8t)"
  # Rust WASM
  if $HAS_RUST_WASM; then
    json_row="$json_row | $(get_formatted "$json_tmp" $json_idx_rust_wasm)"
  fi
  # JS Node
  if $HAS_JS; then
    json_row="$json_row | $(get_formatted "$json_tmp" $json_idx_js)"
  fi
  # libevtx — JSON not supported
  if $HAS_LIBEVTX; then
    json_row="$json_row | No support"
  fi
  # velocidex
  if $HAS_VELOCIDEX; then
    json_row="$json_row | $(get_formatted "$json_tmp" $json_idx_velocidex)"
  fi
  # 0xrawsec
  if $HAS_0XRAWSEC; then
    json_row="$json_row | $(get_formatted "$json_tmp" $json_idx_0xrawsec)"
  fi

  # pyevtx-rs (single run)
  if $HAS_PYEVTX_RS; then
    echo "    pyevtx-rs JSON (single run)..." >&2
    local pyrs_secs
    if pyrs_secs=$(measure_cmd_once_seconds "uv run --with evtx python -c 'import sys, collections; from evtx import PyEvtxParser; p=PyEvtxParser(sys.argv[1]); collections.deque((sys.stdout.write(r[\"data\"] + \"\\n\") for r in p.records_json()), maxlen=0)' '$file' > /dev/null" 2>/dev/null); then
      json_row="$json_row | $(format_single_run "$pyrs_secs")"
      echo "      $(format_single_run "$pyrs_secs")" >&2
    else
      json_row="$json_row | ERR"
    fi
  fi

  # python-evtx CPython — JSON not supported
  if $HAS_PYTHON_EVTX; then
    json_row="$json_row | No support"
  fi
  # python-evtx PyPy — JSON not supported
  if $HAS_PYTHON_EVTX_PYPY; then
    json_row="$json_row | No support"
  fi

  echo "$json_row |"

  rm -f "$json_tmp"
}
