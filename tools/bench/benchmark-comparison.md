# Parser Benchmark Comparison

| Field           | Value                        |
|-----------------|------------------------------|
| **Date**        | 2026-02-19 02:47:13 UTC      |
| **Node**        | v25.6.1                      |
| **dotnet**      | 10.0.101                     |
| **Platform**    | Darwin arm64                 |
| **Rust binary** | `evtx_dump --release`        |
| **Warmup**      | 5                            |
| **Runs**        | 10                           |
| **libevtx (C)** | evtxexport (single-threaded) |

## Benchmark Results

| File                                | C# (1 thread)      | C# (8 threads)   | C# WASM           | evtx (Rust - 1 thread) | evtx (Rust - 8 threads) | JS Node            | libevtx (C)       |
|-------------------------------------|--------------------|------------------|-------------------|------------------------|-------------------------|--------------------|-------------------|
| 30M security_big_sample.evtx (XML)  | 68.9 ms ± 0.5 ms   | 58.1 ms ± 1.0 ms | N/A (web only)    | 155.4 ms ± 0.4 ms      | 54.6 ms ± 2.5 ms        | 976.4 ms ± 15.8 ms | 1.397 s ± 0.029 s |
| 30M security_big_sample.evtx (JSON) | 195.2 ms ± 21.7 ms | 86.7 ms ± 7.5 ms | 112.8 ms ± 1.1 ms | 140.3 ms ± 0.6 ms      | 49.6 ms ± 0.4 ms        | 1.306 s ± 0.012 s  | No support        |

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
