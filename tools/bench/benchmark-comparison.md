# Parser Benchmark Comparison

| Field | Value |
|-------|-------|
| **Date** | 2026-02-17 05:46:17 UTC |
| **Node** | v25.6.1 |
| **dotnet** | 10.0.101 |
| **Platform** | Darwin arm64 |
| **Rust binary** | `evtx_dump --release` |
| **Warmup** | 5 |
| **Runs** | 10 |
| **libevtx (C)** | evtxexport (single-threaded) |

## Benchmark Results

| File | C# (1 thread) | C# (8 threads) | C# WASM | evtx (Rust - 1 thread) | evtx (Rust - 8 threads) | JS Node | libevtx (C) |
|----------------------|----------------------|----------------------|----------------------|----------------------|----------------------|----------------------|----------------------|
| 30M security_big_sample.evtx (XML) | 109.1 ms ± 6.6 ms | 81.9 ms ± 5.2 ms | N/A (web only) | 305.0 ms ± 2.3 ms | 88.3 ms ± 15.8 ms | 1.922 s ± 0.040 s | 2.702 s ± 0.046 s |
| 30M security_big_sample.evtx (JSON) | 128.2 ms ± 4.4 ms | 94.3 ms ± 1.8 ms | 215.5 ms ± 2.3 ms | 273.7 ms ± 2.6 ms | 98.9 ms ± 7.8 ms | 4.188 s ± 1.276 s | No support |

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
