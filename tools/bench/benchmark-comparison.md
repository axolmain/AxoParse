# Parser Benchmark Comparison

| Field           | Value                        |
|-----------------|------------------------------|
| **Date**        | 2026-02-16 20:56:34 UTC      |
| **Node**        | v25.6.1                      |
| **dotnet**      | 10.0.101                     |
| **Platform**    | Darwin arm64                 |
| **Rust binary** | `evtx_dump --release`        |
| **Warmup**      | 5                            |
| **Runs**        | 10                           |
| **libevtx (C)** | evtxexport (single-threaded) |

## Benchmark Results

| File                                | C# (1 thread)     | C# (8 threads)   | C# WASM           | evtx (Rust - 1 thread) | evtx (Rust - 8 threads) | JS Node           | libevtx (C)       |
|-------------------------------------|-------------------|------------------|-------------------|------------------------|-------------------------|-------------------|-------------------|
| 30M security_big_sample.evtx (XML)  | 107.0 ms ± 7.2 ms | 77.6 ms ± 0.8 ms | N/A (web only)    | 304.3 ms ± 1.8 ms      | 75.7 ms ± 0.8 ms        | 1.901 s ± 0.014 s | 2.662 s ± 0.023 s |
| 30M security_big_sample.evtx (JSON) | 126.7 ms ± 3.5 ms | 94.8 ms ± 1.9 ms | 215.5 ms ± 1.4 ms | 274.5 ms ± 1.7 ms      | 94.8 ms ± 1.8 ms        | 2.574 s ± 0.024 s | No support        |

**Note**: Numbers shown are `real-time` measurements (wall-clock time for invocation to complete). Single-run entries
are marked with *(ran once)* — these parsers are too slow for repeated benchmarking via hyperfine.

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
