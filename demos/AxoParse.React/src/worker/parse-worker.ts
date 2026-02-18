import type {WorkerRequest, WorkerResponse} from "./messages"
import type {EvtxRecord, RecordMeta} from "../types"
import {
    EVTX_CHUNK_SIZE,
    EVTX_HEADER_SIZE,
    getRecordPage,
    initAxoParse,
    parseChunkMetadata,
    parseEvtxFile,
    parseFileHeader,
    renderRecord,
} from "../axoparse"

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

        case "parseStream": {
            try {
                const file = msg.file

                // Step 1: Parse file header
                const t0 = performance.now()
                const headerBuf = await file.slice(0, EVTX_HEADER_SIZE).arrayBuffer()
                const meta = parseFileHeader(new Uint8Array(headerBuf))
                const parseTimeMs = Math.round(performance.now() - t0)

                respond({type: "fileMeta", meta, fileName: msg.fileName, parseTimeMs})

                // Step 2: Compute actual chunk count from file size (header numChunks is a ushort, max 65535)
                const actualChunkCount = Math.floor((file.size - meta.headerBlockSize) / EVTX_CHUNK_SIZE)
                const totalChunks = Math.max(meta.numChunks, actualChunkCount)

                // Step 3: Stream chunks one at a time
                let totalRecords = 0
                for (let i = 0; i < totalChunks; i++) {
                    const offset = meta.headerBlockSize + i * EVTX_CHUNK_SIZE
                    const end = offset + EVTX_CHUNK_SIZE
                    const sliceBuf = await file.slice(offset, Math.min(end, file.size)).arrayBuffer()

                    // Zero-pad if the last chunk is truncated
                    let chunkBytes: Uint8Array
                    if (sliceBuf.byteLength < EVTX_CHUNK_SIZE) {
                        chunkBytes = new Uint8Array(EVTX_CHUNK_SIZE)
                        chunkBytes.set(new Uint8Array(sliceBuf))
                    } else {
                        chunkBytes = new Uint8Array(sliceBuf)
                    }

                    const records: RecordMeta[] = parseChunkMetadata(chunkBytes, i)
                    totalRecords += records.length

                    respond({
                        type: "chunkRecords",
                        records,
                        chunkIndex: i,
                        chunksProcessed: i + 1,
                        totalChunks,
                    })
                }

                respond({type: "streamDone", totalRecords})
            } catch (err: unknown) {
                const message = err instanceof Error ? err.message : String(err)
                respond({type: "error", message: `Stream parse failed: ${message}`})
            }
            break
        }

        case "renderRecord": {
            try {
                const file = msg.file
                const offset = EVTX_HEADER_SIZE + msg.chunkIndex * EVTX_CHUNK_SIZE
                const end = offset + EVTX_CHUNK_SIZE
                const sliceBuf = await file.slice(offset, Math.min(end, file.size)).arrayBuffer()

                // Zero-pad if truncated
                let chunkBytes: Uint8Array
                if (sliceBuf.byteLength < EVTX_CHUNK_SIZE) {
                    chunkBytes = new Uint8Array(EVTX_CHUNK_SIZE)
                    chunkBytes.set(new Uint8Array(sliceBuf))
                } else {
                    chunkBytes = new Uint8Array(sliceBuf)
                }

                const record = renderRecord(chunkBytes, msg.chunkIndex, msg.recordIndex)

                respond({
                    type: "renderedRecord",
                    record,
                    chunkIndex: msg.chunkIndex,
                    recordIndex: msg.recordIndex,
                })
            } catch (err: unknown) {
                const message = err instanceof Error ? err.message : String(err)
                respond({type: "error", message: `Render record failed: ${message}`})
            }
            break
        }
    }
}
