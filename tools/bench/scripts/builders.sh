#!/usr/bin/env bash
# shellcheck shell=bash
# Functions to fetch and build external parsers for benchmarking.
# Expects: RUST_DIR, RUST_BIN, EXTERNAL_DIR, CS_WASM_PROJECT, CS_WASM_FRAMEWORK,
#          JS_REPO_DIR, JS_CLI set by bench-all.sh

fetch_and_build_rust_evtx() {
  log_info "Setting up Rust evtx parser..."
  local dir="$RUST_DIR"

  if [[ ! -d "$dir" ]]; then
    log_info "Cloning omerbenamram/evtx..."
    git clone --quiet https://github.com/omerbenamram/evtx.git "$dir"
  fi

  if [[ ! -f "$RUST_BIN" ]] || ! binary_looks_compatible "$RUST_BIN"; then
    log_info "Building Rust evtx (release)..."
    cargo build --release --manifest-path "$dir/Cargo.toml" 2>/dev/null
  fi

  log_success "Rust evtx ready"
}

build_cs_wasm() {
  log_info "Building C# WASM (AOT)..."

  if [[ ! -f "$CS_WASM_PROJECT" ]]; then
    log_warn "C# WASM project not found at $CS_WASM_PROJECT"
    return 1
  fi

  local out_dir="$EXTERNAL_DIR/cs-wasm"
  mkdir -p "$out_dir"

  if ! dotnet publish "$CS_WASM_PROJECT" -c Release -o "$out_dir" --nologo -v quiet 2>/dev/null; then
    return 1
  fi

  # Locate _framework in publish output (path varies by SDK version)
  local found
  found=$(find "$out_dir" -type d -name "_framework" -print -quit 2>/dev/null)
  if [[ -n "$found" && -d "$found" ]]; then
    CS_WASM_FRAMEWORK="$found"
    log_success "C# WASM ready at $CS_WASM_FRAMEWORK"
  else
    log_warn "C# WASM _framework directory not found in publish output"
    return 1
  fi
}

build_rust_wasm() {
  log_info "Building Rust WASM package..."
  local wasm_dir="$RUST_DIR/evtx-wasm"

  if [[ ! -d "$wasm_dir" ]]; then
    log_warn "evtx-wasm crate not found at $wasm_dir"
    return 1
  fi

  if ! command -v wasm-pack &>/dev/null; then
    log_warn "wasm-pack not found — install with: cargo install wasm-pack"
    return 1
  fi

  wasm-pack build --target nodejs --release "$wasm_dir" 2>/dev/null
  log_success "Rust WASM ready"
}

fetch_and_build_libevtx() {
  log_info "Setting up C libevtx..."
  local dir="$EXTERNAL_DIR/libevtx"

  if [[ ! -d "$dir" ]]; then
    log_info "Cloning libevtx..."
    git clone --quiet https://github.com/libyal/libevtx.git "$dir"
  fi

  (
    cd "$dir" || return 1

    if [[ ! -f "evtxtools/evtxexport" ]] || ! binary_looks_compatible "evtxtools/evtxexport"; then
      log_info "Building libevtx (this may take a while)..."
      rm -f evtxtools/evtxexport 2>/dev/null || true

      [[ ! -d "libcerror" ]] && ./synclibs.sh 2>/dev/null
      [[ ! -f "configure" ]] && ./autogen.sh 2>/dev/null

      ./configure --enable-static --disable-shared --enable-static-executables \
        --quiet 2>/dev/null
      make -j"$(ncpus)" --quiet 2>/dev/null
    fi
  )

  log_success "C libevtx ready"
}

fetch_and_build_velocidex() {
  log_info "Setting up Go Velocidex evtx..."
  local dir="$EXTERNAL_DIR/velocidex-evtx"

  if [[ ! -d "$dir" ]]; then
    log_info "Cloning Velocidex evtx..."
    git clone --quiet https://github.com/Velocidex/evtx.git "$dir"
  fi

  (
    cd "$dir" || return 1

    if [[ ! -f "dumpevtx" ]] || ! binary_looks_compatible "dumpevtx"; then
      log_info "Building Velocidex dumpevtx..."
      go build -o dumpevtx ./cmd/ 2>/dev/null
    fi
  )

  log_success "Go Velocidex ready"
}

fetch_and_build_0xrawsec() {
  log_info "Setting up Go 0xrawsec evtx..."
  local dir="$EXTERNAL_DIR/0xrawsec-evtx"

  if [[ ! -d "$dir" ]]; then
    log_info "Cloning 0xrawsec evtx..."
    git clone --quiet https://github.com/0xrawsec/golang-evtx.git "$dir"
  fi

  (
    cd "$dir" || return 1

    if [[ ! -f "evtxdump" ]] || ! binary_looks_compatible "evtxdump"; then
      log_info "Building 0xrawsec evtxdump..."

      # Patch missing Version/CommitID constants if needed
      if ! grep -q "Version.*=.*\"" tools/evtxdump/evtxdump.go 2>/dev/null; then
        local tmpf
        tmpf=$(mktemp)
        if sed '/^const (/,/^)/{
          /conditions;`$/a\
	Version  = "dev"\
	CommitID = "unknown"
        }' tools/evtxdump/evtxdump.go > "$tmpf" 2>/dev/null; then
          mv "$tmpf" tools/evtxdump/evtxdump.go
        else
          rm -f "$tmpf"
        fi
      fi

      go build -o evtxdump ./tools/evtxdump/ 2>/dev/null
    fi
  )

  log_success "Go 0xrawsec ready"
}

fetch_and_build_js_evtx() {
  log_info "Setting up JS evtx parser..."
  local dir="$JS_REPO_DIR"

  if [[ ! -d "$dir" ]]; then
    log_info "Cloning axolmain/evtx-parser..."
    git clone --quiet https://github.com/axolmain/evtx-parser.git "$dir"
  fi

  if [[ ! -d "$dir/node_modules" ]]; then
    log_info "Installing JS dependencies..."
    npm install --prefix "$dir" --silent
  fi

  log_info "Bundling bench-cli.ts → bench-cli.mjs..."
  npx --yes esbuild "$dir/tests/bench/bench-cli.ts" --bundle --platform=node --format=esm \
    --outfile="$JS_CLI" --log-level=warning

  log_success "JS evtx parser ready at $JS_CLI"
}
