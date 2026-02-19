import type {EvtxRecord, FileMeta, ParseMeta, RecordMeta} from "./types"

/** Size of the EVTX file header in bytes. */
export const EVTX_HEADER_SIZE = 4096

/** Size of each EVTX chunk in bytes (64 KB). */
export const EVTX_CHUNK_SIZE = 65536

/** Interop function signatures exposed by the .NET WASM module. */
interface WasmExports {
    ParseEvtxFile: (data: Uint8Array) => string
    GetRecordPage: (offset: number, limit: number) => string
    ParseEvtxToJson: (data: Uint8Array) => string
    ParseFileHeader: (headerData: Uint8Array) => string
    ParseChunkMetadata: (chunkData: Uint8Array, chunkIndex: number) => string
    RenderRecord: (chunkData: Uint8Array, chunkIndex: number, recordIndex: number) => string
}

let exports: WasmExports | null = null

/**
 * Initialise the AxoParse WASM module.
 * Must be called once before any other function in this module.
 *
 * @param frameworkUrl - URL path to the `_framework/` directory produced by
 *   `dotnet publish src/AxoParse.Browser -c Release`.
 *   When the output is copied into `public/wasm/` this is typically
 *   `"/wasm/_framework"`.
 *
 * @example
 * ```ts
 * await initAxoParse("/wasm/_framework")
 * ```
 */
export async function initAxoParse(frameworkUrl: string): Promise<void> {
    // Mark this as a "sidecar" worker so the .NET runtime doesn't mistake it
    // for a .NET-managed pthread. Without this flag the runtime skips resolving
    // coreAssetsInMemory / allAssetsInMemory and dotnet.create() hangs forever.
    if (typeof importScripts === "function") {
        (globalThis as Record<string, unknown>).dotnetSidecar = true
    }
    const {dotnet} = await import(/* @vite-ignore */ `${frameworkUrl}/dotnet.js`)
    const runtime = await dotnet.create()
    await runtime.runMain()
    const config = runtime.getConfig()
    const asm = await runtime.getAssemblyExports(config.mainAssemblyName!)
    exports = asm.AxoParse.Browser.EvtxInterop
}

/**
 * Parse an EVTX file and store the result in WASM memory.
 * Returns lightweight metadata only — no record data is serialised.
 * Use {@link getRecordPage} or {@link streamRecords} to retrieve records afterwards.
 *
 * @param data - Raw EVTX file bytes (e.g. from `FileReader.readAsArrayBuffer`).
 * @returns Metadata including total record and chunk counts.
 *
 * @example
 * ```ts
 * const meta = parseEvtxFile(new Uint8Array(buffer))
 * console.log(`Parsed ${meta.totalRecords} records`)
 * ```
 */
export function parseEvtxFile(data: Uint8Array): ParseMeta {
    if (!exports) throw new Error("WASM not initialised — call initAxoParse() first")
    return JSON.parse(exports.ParseEvtxFile(data)) as ParseMeta
}

/**
 * Fetch a single page of records from the WASM-stored parse result.
 * Must call {@link parseEvtxFile} first.
 *
 * @param offset - Zero-based starting record index.
 * @param limit  - Maximum number of records to return.
 * @returns Array of record objects for the requested range.
 *
 * @example
 * ```ts
 * const firstPage = getRecordPage(0, 100)
 * const secondPage = getRecordPage(100, 100)
 * ```
 */
export function getRecordPage(offset: number, limit: number): EvtxRecord[] {
    if (!exports) throw new Error("WASM not initialised — call initAxoParse() first")
    return JSON.parse(exports.GetRecordPage(offset, limit)) as EvtxRecord[]
}

/**
 * Parse the 4096-byte EVTX file header and return metadata.
 *
 * @param headerData - First 4096 bytes of the EVTX file.
 * @returns Parsed file header metadata.
 */
export function parseFileHeader(headerData: Uint8Array): FileMeta {
    if (!exports) throw new Error("WASM not initialised — call initAxoParse() first")
    return JSON.parse(exports.ParseFileHeader(headerData)) as FileMeta
}

/**
 * Parse a single 64KB chunk and return lightweight metadata for each record.
 *
 * @param chunkData - Exactly 65536 bytes covering one chunk.
 * @param chunkIndex - Zero-based chunk index.
 * @returns Array of lightweight record metadata objects.
 */
export function parseChunkMetadata(chunkData: Uint8Array, chunkIndex: number): RecordMeta[] {
    if (!exports) throw new Error("WASM not initialised — call initAxoParse() first")
    return JSON.parse(exports.ParseChunkMetadata(chunkData, chunkIndex)) as RecordMeta[]
}

/**
 * Re-parse a chunk and return full detail JSON for a single record.
 * Used for on-demand rendering when the user expands a table row.
 *
 * @param chunkData - Exactly 65536 bytes covering one chunk.
 * @param chunkIndex - Zero-based chunk index.
 * @param recordIndex - Zero-based index of the record within the chunk.
 * @returns Full record object with all fields including XML and eventData.
 */
export function renderRecord(chunkData: Uint8Array, chunkIndex: number, recordIndex: number): EvtxRecord {
    if (!exports) throw new Error("WASM not initialised — call initAxoParse() first")
    return JSON.parse(exports.RenderRecord(chunkData, chunkIndex, recordIndex)) as EvtxRecord
}
