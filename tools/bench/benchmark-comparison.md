# Parser Benchmark Comparison

| Field | Value |
|-------|-------|
| **Date** | 2026-10-15 22:06:56 UTC |
| **Node** | v26.11.1 |
| **dotnet** | 10.0.101 |
| **Platform** | Darwin arm64 |
| **Rust binary** | `evtx_dump --release` |
| **Warmup** | 5 |
| **Runs** | 10 |
| **libevtx (C)** | evtxexport (single-threaded) |

## Benchmark Results

| File | C# (1 thread) | C# (8 threads) | C# WASM (paged JSON) | evtx (Rust - 1 thread) | evtx (Rust - 8 threads) | Rust WASM (Arrow IPC) | JS Node | libevtx (C) |
|----------------------|----------------------|----------------------|----------------------|----------------------|----------------------|----------------------|----------------------|----------------------|
| 30M security_big_sample.evtx (XML) | 36.1 ms ± 2.4 ms | 16.4 ms ± 0.7 ms | N/A (web only) | 38.5 ms ± 0.3 ms | 9.7 ms ± 0.3 ms | N/A (web only) | 930.7 ms ± 13.1 ms | 1.396 s ± 0.016 s |
| 30M security_big_sample.evtx (JSON) | 40.0 ms ± 0.9 ms | 18.6 ms ± 0.8 ms | 1.165 s ± 0.005 s | 37.4 ms ± 0.3 ms | 9.3 ms ± 0.4 ms | 748.4 ms ± 3.8 ms | 1.129 s ± 0.011 s | No support |

**Note**: Numbers shown are `real-time` measurements (wall-clock time for invocation to complete). Single-run entries are marked with *(ran once)* — these parsers are too slow for repeated benchmarking via hyperfine.

## Summary

- **Files tested:** 1
- **Passed:** 1
- **Failed (JS):** 0

### Internal parsers
- C# native: yes
- Rust native: yes
- JS Node: yes
- Rust WASM: yes
- C# WASM (AOT): yes

### External parsers
- libevtx (C): yes
