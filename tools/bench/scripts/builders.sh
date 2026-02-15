#!/usr/bin/env bash
# Functions to fetch and build external parsers for benchmarking.
# Expects: RUST_DIR, RUST_BIN, EXTERNAL_DIR set by bench-all.sh

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

  local prev_dir="$PWD"
  cd "$dir"

  if [[ ! -f "evtxtools/evtxexport" ]] || ! binary_looks_compatible "evtxtools/evtxexport"; then
    log_info "Building libevtx (this may take a while)..."
    rm -f evtxtools/evtxexport 2>/dev/null || true

    if [[ ! -d "libcerror" ]]; then
      ./synclibs.sh 2>/dev/null
    fi

    if [[ ! -f "configure" ]]; then
      ./autogen.sh 2>/dev/null
    fi

    ./configure --enable-static --disable-shared --enable-static-executables \
      --quiet 2>/dev/null
    make -j"$(sysctl -n hw.ncpu 2>/dev/null || nproc)" --quiet 2>/dev/null
  fi

  cd "$prev_dir"
  log_success "C libevtx ready"
}

fetch_and_build_velocidex() {
  log_info "Setting up Go Velocidex evtx..."
  local dir="$EXTERNAL_DIR/velocidex-evtx"

  if [[ ! -d "$dir" ]]; then
    log_info "Cloning Velocidex evtx..."
    git clone --quiet https://github.com/Velocidex/evtx.git "$dir"
  fi

  local prev_dir="$PWD"
  cd "$dir"

  if [[ ! -f "dumpevtx" ]] || ! binary_looks_compatible "dumpevtx"; then
    log_info "Building Velocidex dumpevtx..."
    go build -o dumpevtx ./cmd/ 2>/dev/null
  fi

  cd "$prev_dir"
  log_success "Go Velocidex ready"
}

fetch_and_build_0xrawsec() {
  log_info "Setting up Go 0xrawsec evtx..."
  local dir="$EXTERNAL_DIR/0xrawsec-evtx"

  if [[ ! -d "$dir" ]]; then
    log_info "Cloning 0xrawsec evtx..."
    git clone --quiet https://github.com/0xrawsec/golang-evtx.git "$dir"
  fi

  local prev_dir="$PWD"
  cd "$dir"

  if [[ ! -f "evtxdump" ]] || ! binary_looks_compatible "evtxdump"; then
    log_info "Building 0xrawsec evtxdump..."

    # Fix missing Version/CommitID if needed
    if ! grep -q "Version.*=.*\"" tools/evtxdump/evtxdump.go 2>/dev/null; then
      sed -i.bak '/^const (/,/^)/{
        /conditions;`$/a\
	Version  = "dev"\
	CommitID = "unknown"
      }' tools/evtxdump/evtxdump.go 2>/dev/null || true
    fi

    go build -o evtxdump ./tools/evtxdump/ 2>/dev/null
  fi

  cd "$prev_dir"
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

fetch_and_setup_python_evtx() {
  log_info "Setting up Python python-evtx..."
  local dir="$EXTERNAL_DIR/python-evtx"

  if [[ ! -d "$dir" ]]; then
    log_info "Cloning python-evtx..."
    git clone --quiet https://github.com/williballenthin/python-evtx.git "$dir"
  fi

  local prev_dir="$PWD"
  cd "$dir"

  # Setup with CPython
  if [[ ! -d ".venv-cpython" ]]; then
    log_info "Setting up CPython venv..."
    uv venv --python 3.13 .venv-cpython 2>/dev/null
    uv pip install --quiet -p .venv-cpython/bin/python -e . 2>/dev/null
  fi

  # Setup with PyPy
  if [[ ! -d ".venv-pypy" ]]; then
    log_info "Setting up PyPy venv..."
    uv python install pypy3.10 2>/dev/null || true
    if uv venv --python pypy3.10 .venv-pypy 2>/dev/null; then
      uv pip install --quiet -p .venv-pypy/bin/python -e . 2>/dev/null
    else
      log_warn "PyPy setup failed, skipping"
    fi
  fi

  cd "$prev_dir"
  log_success "Python python-evtx ready"
}
