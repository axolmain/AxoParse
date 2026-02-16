# Parser Benchmark Comparison

| Field | Value |
|-------|-------|
| **Date** | 2026-02-16 02:37:05 UTC |
| **Node** | v25.6.0 |
| **dotnet** | 10.0.101 |
| **Platform** | Darwin arm64 |
| **Rust binary** | `evtx_dump --release` |
| **Warmup** | 5 |
| **Runs** | 10 |
| **libevtx (C)** | evtxexport (single-threaded) |

## Benchmark Results

| File | C# (1 thread) | C# (8 threads) | C# WASM | evtx (Rust - 1 thread) | evtx (Rust - 8 threads) | JS Node | libevtx (C) |
|----------------------|----------------------|----------------------|----------------------|----------------------|----------------------|----------------------|----------------------|
| 30M security_big_sample.evtx (XML) | 105.3 ms ± 5.4 ms | 85.6 ms ± 2.4 ms | N/A (web only) | 157.4 ms ± 1.2 ms | 57.3 ms ± 1.7 ms | 1.013 s ± 0.013 s | 1.422 s ± 0.055 s |
| 30M security_big_sample.evtx (JSON) | 267.2 ms ± 3.5 ms | 134.1 ms ± 1.9 ms | 115.7 ms ± 1.4 ms | 146.4 ms ± 7.2 ms | 53.6 ms ± 1.4 ms | 1.337 s ± 0.010 s | No support |

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
