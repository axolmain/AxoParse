import type {EvtxRecord, ParseMeta} from "./types"

/** Interop function signatures exposed by the .NET WASM module. */
interface WasmExports {
    ParseEvtxFile: (data: Uint8Array) => string
    GetRecordPage: (offset: number, limit: number) => string
    ParseEvtxToJson: (data: Uint8Array) => string
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


