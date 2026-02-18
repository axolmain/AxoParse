import type {WorkerRequest, WorkerResponse} from "./messages"
import type {EvtxRecord} from "../types"
import {getRecordPage, initAxoParse, parseEvtxFile} from "../axoparse"

/** Records fetched per WASM interop call. */
const PAGE_SIZE = 500

/** Number of records in the first batch sent for immediate rendering. */
const PREVIEW_SIZE = 500

/** Post a typed response back to the main thread. */
function respond(msg: WorkerResponse): void {
    postMessage(msg)
}

self.onmessage = async (e: MessageEvent<WorkerRequest>) => {
    const msg = e.data

    switch (msg.type) {
        case "init": {
            try {
                await initAxoParse(msg.frameworkUrl)
                respond({type: "ready"})
            } catch (err: unknown) {
                const message = err instanceof Error ? err.message : String(err)
                respond({type: "error", message: `Failed to load WASM module: ${message}`})
            }
            break
        }

        case "parse": {
            try {
                const data = new Uint8Array(msg.data)

                const t0 = performance.now()
                const meta = parseEvtxFile(data)
                const parseTimeMs = Math.round(performance.now() - t0)

                respond({
                    type: "meta",
                    totalRecords: meta.totalRecords,
                    numChunks: meta.numChunks,
                    parseTimeMs,
                    fileName: msg.fileName,
                })

                // Send a small preview batch so the main thread can render rows immediately
                const preview = getRecordPage(0, PREVIEW_SIZE)
                respond({type: "preview", records: preview})

                // Continue fetching the rest, sending lightweight progress updates
                const allRecords: EvtxRecord[] = new Array(meta.totalRecords)
                for (let i = 0; i < preview.length; i++) {
                    allRecords[i] = preview[i]
                }
                let written = preview.length

                for (let offset = written; offset < meta.totalRecords; offset += PAGE_SIZE) {
                    const page = getRecordPage(offset, PAGE_SIZE)
                    for (let i = 0; i < page.length; i++) {
                        allRecords[written++] = page[i]
                    }
                    respond({type: "progress", loaded: written})
                }

                allRecords.length = written
                respond({type: "records", records: allRecords})
            } catch (err: unknown) {
                const message = err instanceof Error ? err.message : String(err)
                respond({type: "error", message: `Parse failed: ${message}`})
            }
            break
        }
    }
}
