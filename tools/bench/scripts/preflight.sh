#!/usr/bin/env bash
# shellcheck shell=bash
# Preflight checks: verify required tools, build parsers, set HAS_* flags.
# Expects all path variables, NATIVE_ONLY, NO_BUILD, PARSER_FILTER set by bench-all.sh.

run_preflight() {
  # ── Required tools ──────────────────────────────────────────────────
  if ! command -v hyperfine &>/dev/null; then
    log_error "hyperfine not found. Install with: brew install hyperfine"
    exit 1
  fi

  if ! $NO_BUILD && ! command -v cargo &>/dev/null; then
    log_error "cargo not found. Install Rust: https://rustup.rs"
    exit 1
  fi

  # ── Build Rust evtx parser ──────────────────────────────────────────
  mkdir -p "$EXTERNAL_DIR"

  if ! $NO_BUILD; then
    fetch_and_build_rust_evtx
  fi

  if [[ ! -x "$RUST_BIN" ]]; then
    log_error "Rust binary not found at $RUST_BIN"
    exit 1
  fi

  # ── JS parser (external repo) ─────────────────────────────────────
  HAS_JS=false
  if ! $NATIVE_ONLY; then
    if ! $NO_BUILD; then
      fetch_and_build_js_evtx
    fi
    if [[ -f "$JS_CLI" ]]; then
      HAS_JS=true
      log_success "JS bundle ready at $JS_CLI"
    else
      log_warn "JS bundle not found at $JS_CLI — skipping JS benchmarks"
    fi
  fi

  # ── C# native build ────────────────────────────────────────────────
  HAS_CSHARP=false
  if [[ -f "$CSHARP_PROJECT" ]]; then
    if ! $NO_BUILD; then
      log_info "Building C# bench binary..."
      if dotnet publish "$CSHARP_PROJECT" -c Release -o "$(dirname "$CSHARP_BIN")" --nologo -v quiet 2>/dev/null; then
        HAS_CSHARP=true
        log_success "C# binary ready at $CSHARP_BIN"
      else
        log_warn "C# build failed — skipping C# benchmarks"
      fi
    elif [[ -x "$CSHARP_BIN" ]]; then
      HAS_CSHARP=true
      log_info "C# binary ready at $CSHARP_BIN (existing)"
    fi
  fi

  # ── WASM / external parser flags ────────────────────────────────────
  HAS_CS_WASM=false
  HAS_RUST_WASM=false
  HAS_LIBEVTX=false

  if ! $NATIVE_ONLY; then
    # ── C# WASM ──
    if ! $NO_BUILD && [[ ! -d "$CS_WASM_FRAMEWORK" ]]; then
      build_cs_wasm || log_warn "C# WASM build failed"
    fi
    if [[ -d "$CS_WASM_FRAMEWORK" && -f "$CS_WASM_CLI" ]]; then
      HAS_CS_WASM=true
      log_success "C# WASM benchmark ready"
    fi

    # ── Rust WASM ──
    if ! $NO_BUILD && [[ ! -f "$RUST_WASM_PKG/evtx_wasm.js" ]]; then
      build_rust_wasm || log_warn "Rust WASM build failed"
    fi
    if [[ -f "$RUST_WASM_PKG/evtx_wasm.js" && -f "$RUST_WASM_CLI" ]]; then
      HAS_RUST_WASM=true
      log_success "Rust WASM benchmark ready"
    fi

    # ── libevtx (C) ──
    if command -v git &>/dev/null && command -v make &>/dev/null; then
      if ! $NO_BUILD; then
        fetch_and_build_libevtx || log_warn "libevtx build failed"
      fi
      [[ -x "$EXTERNAL_DIR/libevtx/evtxtools/evtxexport" ]] && HAS_LIBEVTX=true
    else
      log_warn "git/make not found — skipping libevtx benchmark"
    fi

  else
    log_info "Running in --native-only mode (C# + Rust only)"
  fi

  # ── Apply --parsers filter (Rust native is always included as baseline) ──
  if [[ -n "${PARSER_FILTER:-}" ]]; then
    local ALL_PARSERS=(csharp cs-wasm rust-wasm js libevtx)
    for name in "${ALL_PARSERS[@]}"; do
      if ! parser_in_filter "$name"; then
        case "$name" in
          csharp)    HAS_CSHARP=false ;;
          cs-wasm)   HAS_CS_WASM=false ;;
          rust-wasm) HAS_RUST_WASM=false ;;
          js)        HAS_JS=false ;;
          libevtx)   HAS_LIBEVTX=false ;;
        esac
      fi
    done
  fi
}
