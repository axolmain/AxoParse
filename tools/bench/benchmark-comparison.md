# Parser Benchmark Comparison

| Field | Value |
|-------|-------|
| **Date** | 2026-02-15 10:23:45 UTC |
| **Node** | v25.6.0 |
| **dotnet** | 10.0.101 |
| **Platform** | Darwin arm64 |
| **Rust binary** | `evtx_dump --release` |
| **Warmup** | 5 |
| **Runs** | 10 |
| **libevtx (C)** | evtxexport (single-threaded) |
| **Velocidex (Go)** | dumpevtx |
| **0xrawsec (Go)** | evtxdump |
| **pyevtx-rs** | via `uv run --with evtx` |

## Benchmark Results

| File | C# (1 thread) | C# (8 threads) | C# WASM | evtx (Rust - 1 thread) | evtx (Rust - 8 threads) | JS Node | libevtx (C) | velocidex/evtx (Go) | golang-evtx (Go) | pyevtx-rs |
|----------------------|----------------------|----------------------|----------------------|----------------------|----------------------|----------------------|----------------------|----------------------|----------------------|----------------------|
| 30M security_big_sample.evtx (XML) | 119.3 ms ± 3.4 ms | 71.9 ms ± 0.6 ms | N/A (web only) | 302.3 ms ± 1.7 ms | 126.2 ms ± 83.3 ms | 1.947 s ± 0.017 s | 2.697 s ± 0.029 s | No support | No support | ERR |
| 30M security_big_sample.evtx (JSON) | 326.8 ms ± 7.0 ms | 153.7 ms ± 3.6 ms |  |  |  |  | No support |  |  | ERR |

**Note**: Numbers shown are `real-time` measurements (wall-clock time for invocation to complete). Single-run entries are marked with *(ran once)* — these parsers are too slow for repeated benchmarking via hyperfine.

## Summary

- **Files tested:** 1
- **Passed:** 1
- **Failed (JS):** 0

### Internal parsers
- C# native: yes
- Rust native: yes
- JS Node: yes
- C# WASM (AOT): yes

### External parsers
- libevtx (C): yes
- Velocidex (Go): yes
- 0xrawsec (Go): yes
- pyevtx-rs: yes
