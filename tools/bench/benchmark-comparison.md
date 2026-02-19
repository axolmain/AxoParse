# Parser Benchmark Comparison

| Field           | Value                        |
|-----------------|------------------------------|
| **Date**        | 2026-02-19 00:12:33 UTC      |
| **Node**        | v25.6.1                      |
| **dotnet**      | 10.0.101                     |
| **Platform**    | Darwin arm64                 |
| **Rust binary** | `evtx_dump --release`        |
| **Warmup**      | 5                            |
| **Runs**        | 10                           |
| **libevtx (C)** | evtxexport (single-threaded) |

## Benchmark Results

| File                                | C# (1 thread)     | C# (8 threads)     | C# WASM            | evtx (Rust - 1 thread) | evtx (Rust - 8 threads) | JS Node           | libevtx (C)       |
|-------------------------------------|-------------------|--------------------|--------------------|------------------------|-------------------------|-------------------|-------------------|
| 30M security_big_sample.evtx (XML)  | 131.5 ms ± 7.3 ms | 98.5 ms ± 3.2 ms   | N/A (web only)     | 304.5 ms ± 1.5 ms      | 104.8 ms ± 3.0 ms       | 1.933 s ± 0.052 s | 2.744 s ± 0.072 s |
| 30M security_big_sample.evtx (JSON) | 334.5 ms ± 6.1 ms | 208.1 ms ± 91.7 ms | 262.0 ms ± 73.9 ms | 286.5 ms ± 25.1 ms     | 89.1 ms ± 21.0 ms       | 2.598 s ± 0.010 s | No support        |

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
