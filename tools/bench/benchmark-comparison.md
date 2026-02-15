# Parser Benchmark Comparison

| Field | Value |
|-------|-------|
| **Date** | 2026-02-15 18:13:14 UTC |
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
| 30M security_big_sample.evtx (XML) | 58.9 ms ± 2.2 ms | 47.5 ms ± 4.7 ms | N/A (web only) | 155.3 ms ± 0.7 ms | 56.0 ms ± 0.6 ms | 1.005 s ± 0.017 s | 1.377 s ± 0.019 s | No support | No support | ERR |
| 30M security_big_sample.evtx (JSON) | 165.6 ms ± 1.6 ms | 94.8 ms ± 2.5 ms | 122.1 ms ± 13.0 ms | 139.8 ms ± 1.0 ms | 53.4 ms ± 2.6 ms | 1.332 s ± 0.021 s | No support | 2.212 s ± 0.017 s | 873.8 ms ± 18.5 ms | ERR |

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
