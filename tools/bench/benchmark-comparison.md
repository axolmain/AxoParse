# Parser Benchmark Comparison

| Field | Value |
|-------|-------|
| **Date** | 2026-02-15 09:46:36 UTC |
| **Node** | v25.6.0 |
| **dotnet** | 10.0.101 |
| **Platform** | Darwin arm64 |
| **Rust binary** | `evtx_dump --release` |
| **Warmup** | 5 |
| **Runs** | 10 |
| **libevtx (C)** | evtxexport (single-threaded) |
| **Velocidex (Go)** | dumpevtx |
| **0xrawsec (Go)** | evtxdump |
| **python-evtx** | CPython venv |
| **python-evtx** | PyPy venv |
| **pyevtx-rs** | via `uv run --with evtx` |

