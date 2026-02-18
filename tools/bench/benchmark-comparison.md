# Parser Benchmark Comparison

| Field           | Value                        |
|-----------------|------------------------------|
| **Date**        | 2026-02-18 03:48:23 UTC      |
| **Node**        | v25.6.1                      |
| **dotnet**      | 10.0.101                     |
| **Platform**    | Darwin arm64                 |
| **Rust binary** | `evtx_dump --release`        |
| **Warmup**      | 5                            |
| **Runs**        | 10                           |
| **libevtx (C)** | evtxexport (single-threaded) |

## Benchmark Results

| File                                | C# (1 thread)       | C# (8 threads)      | C# WASM           | evtx (Rust - 1 thread) | evtx (Rust - 8 threads) | JS Node           | libevtx (C)       |
|-------------------------------------|---------------------|---------------------|-------------------|------------------------|-------------------------|-------------------|-------------------|
| 30M security_big_sample.evtx (XML)  | 175.0 ms ± 6.4 ms   | 140.6 ms ± 35.3 ms  | N/A (web only)    | 303.3 ms ± 2.0 ms      | 79.7 ms ± 8.2 ms        | 2.000 s ± 0.131 s | 2.822 s ± 0.098 s |
| 30M security_big_sample.evtx (JSON) | 538.1 ms ± 144.5 ms | 242.1 ms ± 101.8 ms | 215.8 ms ± 4.0 ms | 383.2 ms ± 113.0 ms    | 110.1 ms ± 21.0 ms      | 2.704 s ± 0.319 s | No support        |

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
