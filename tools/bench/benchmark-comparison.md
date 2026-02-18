# Parser Benchmark Comparison

| Field           | Value                        |
|-----------------|------------------------------|
| **Date**        | 2026-02-18 20:51:34 UTC      |
| **Node**        | v25.6.1                      |
| **dotnet**      | 10.0.101                     |
| **Platform**    | Darwin arm64                 |
| **Rust binary** | `evtx_dump --release`        |
| **Warmup**      | 5                            |
| **Runs**        | 10                           |
| **libevtx (C)** | evtxexport (single-threaded) |

## Benchmark Results

| File                                | C# (1 thread)     | C# (8 threads)   | C# WASM           | evtx (Rust - 1 thread) | evtx (Rust - 8 threads) | JS Node            | libevtx (C)       |
|-------------------------------------|-------------------|------------------|-------------------|------------------------|-------------------------|--------------------|-------------------|
| 30M security_big_sample.evtx (XML)  | 77.7 ms ± 1.9 ms  | 70.4 ms ± 1.9 ms | N/A (web only)    | 155.4 ms ± 0.7 ms      | 53.7 ms ± 0.3 ms        | 990.9 ms ± 35.2 ms | 1.381 s ± 0.031 s |
| 30M security_big_sample.evtx (JSON) | 174.4 ms ± 1.2 ms | 82.1 ms ± 1.1 ms | 114.3 ms ± 1.5 ms | 141.3 ms ± 0.7 ms      | 50.2 ms ± 0.6 ms        | 1.486 s ± 0.188 s  | No support        |

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
