# WASM Integration Guide

How to use `AxoParse.Browser` (the WASM build) in your own project.

## Download

Download the `axoparse-wasm-*.tar.gz` from [GitHub Releases](https://github.com/axolmain/AxoParse/releases). It contains the `_framework/` directory with `.wasm` binaries, `.dll` assemblies, and config files needed by the .NET WASM runtime.

## Why it's not a normal JS import

The .NET WASM runtime ships as a `_framework/` directory containing `.wasm` binaries, `.dll` assemblies, and config files. Bundlers like Vite and webpack can't process these — they must be served as **static files** at a URL the browser can fetch at runtime. This means you need to copy `_framework/` into your app's public/static directory.

## CORS Headers

The .NET WASM runtime requires `SharedArrayBuffer`, which browsers only enable with these headers:

```
Cross-Origin-Opener-Policy: same-origin
Cross-Origin-Embedder-Policy: require-corp
```

Configure these on your web server / CDN.

## API Reference

| Function | Description |
|---|---|
| `initAxoParse(frameworkUrl?)` | Initialise the WASM runtime. Call once before any other function. |
| `parseEvtxFile(data)` | Parse EVTX bytes, store in WASM memory. Returns `{ totalRecords, numChunks }`. |
| `getRecordPage(offset, limit)` | Fetch a page of records. Must call `parseEvtxFile` first. |
| `parseEvtxToJson(data)` | Parse and return all records at once (simpler but uses more memory). |

## Record fields

Each record object contains:

| Field | Type | Description |
|---|---|---|
| `recordId` | `number` | Event record ID |
| `timestamp` | `string` | ISO 8601 timestamp |
| `xml` | `string` | Raw XML of the event |
| `chunkIndex` | `number` | Source chunk index |
| `eventId` | `string` | Windows Event ID |
| `provider` | `string` | Event provider name |
| `level` | `number` | Severity level (0-5) |
| `levelText` | `string` | Human-readable level (Critical, Error, Warning, Information, Verbose) |
| `computer` | `string` | Source computer name |
| `channel` | `string` | Event log channel |
| `eventData` | `string` | Extracted event data key-value pairs |
