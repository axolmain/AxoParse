import type { EvtxRecord, ParseMeta } from "./types"

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
 *   `"/wasm/wwwroot/_framework"`.
 *
 * @example
 * ```ts
 * await initAxoParse("/wasm/wwwroot/_framework")
 * ```
 */
export async function initAxoParse(frameworkUrl: string): Promise<void> {
  const { dotnet } = await import(/* @vite-ignore */ `${frameworkUrl}/dotnet.js`)
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

/** Default number of records fetched per page during streaming. */
const DEFAULT_PAGE_SIZE = 500

/**
 * Callback invoked after each page is fetched during {@link streamRecords}.
 *
 * @param records    - All records fetched so far (cumulative).
 * @param totalRows  - Total number of records available.
 */
export type OnPageCallback = (records: EvtxRecord[], totalRows: number) => void

/**
 * Progressively stream all records from WASM memory into a JS array.
 * Calls {@link onPage} after each batch so the UI can render incrementally.
 *
 * Uses `setTimeout(0)` between pages to yield to the browser's render loop,
 * ensuring the first page appears on screen before the rest are fetched.
 *
 * @param totalRows - Total record count from {@link parseEvtxFile}.
 * @param onPage    - Called after each page with the cumulative record array.
 * @param pageSize  - Records per page (default {@link DEFAULT_PAGE_SIZE}).
 * @returns Promise that resolves with the complete record array.
 *
 * @example
 * ```ts
 * const meta = parseEvtxFile(bytes)
 * const allRecords = await streamRecords(meta.totalRecords, (records) => {
 *   setRecords([...records]) // trigger React re-render
 * })
 * ```
 */
export function streamRecords(
  totalRows: number,
  onPage: OnPageCallback,
  pageSize: number = DEFAULT_PAGE_SIZE,
): Promise<EvtxRecord[]> {
  return new Promise((resolve) => {
    const allRecords: EvtxRecord[] = []
    let offset = 0

    function fetchNext(): void {
      const page = getRecordPage(offset, pageSize)
      for (let i = 0; i < page.length; i++) {
        allRecords.push(page[i])
      }
      offset += page.length
      onPage(allRecords, totalRows)

      if (offset < totalRows && page.length > 0) {
        setTimeout(fetchNext, 0)
      } else {
        resolve(allRecords)
      }
    }

    fetchNext()
  })
}
