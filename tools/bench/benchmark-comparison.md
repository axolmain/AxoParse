# Parser Benchmark Comparison

| Field           | Value                        |
|-----------------|------------------------------|
| **Date**        | 2026-02-18 22:49:43 UTC      |
| **Node**        | v25.6.1                      |
| **dotnet**      | 10.0.101                     |
| **Platform**    | Darwin arm64                 |
| **Rust binary** | `evtx_dump --release`        |
| **Warmup**      | 5                            |
| **Runs**        | 10                           |
| **libevtx (C)** | evtxexport (single-threaded) |

## Benchmark Results

| File                                | C# (1 thread)     | C# (8 threads)     | C# WASM           | evtx (Rust - 1 thread) | evtx (Rust - 8 threads) | JS Node           | libevtx (C)       |
|-------------------------------------|-------------------|--------------------|-------------------|------------------------|-------------------------|-------------------|-------------------|
| 30M security_big_sample.evtx (XML)  | 165.4 ms ± 3.0 ms | 148.1 ms ± 41.0 ms | N/A (web only)    | 304.9 ms ± 1.2 ms      | 101.1 ms ± 2.0 ms       | 1.905 s ± 0.022 s | 2.688 s ± 0.034 s |
| 30M security_big_sample.evtx (JSON) | 353.1 ms ± 4.6 ms | 136.3 ms ± 1.2 ms  | 213.6 ms ± 3.1 ms | 277.0 ms ± 5.4 ms      | 96.0 ms ± 2.0 ms        | 2.592 s ± 0.022 s | No support        |

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
