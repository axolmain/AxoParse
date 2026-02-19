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

/** Read a single EVTX chunk from a File, zero-padding if truncated. */
async function readChunk(file: File, chunkIndex: number, headerBlockSize: number): Promise<Uint8Array> {
    const offset = headerBlockSize + chunkIndex * EVTX_CHUNK_SIZE
    const end = offset + EVTX_CHUNK_SIZE
    const sliceBuf = await file.slice(offset, Math.min(end, file.size)).arrayBuffer()

    if (sliceBuf.byteLength < EVTX_CHUNK_SIZE) {
        const padded = new Uint8Array(EVTX_CHUNK_SIZE)
        padded.set(new Uint8Array(sliceBuf))
        return padded
    }
    return new Uint8Array(sliceBuf)
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
                const fileId = msg.fileId

                // Step 1: Parse file header
                const t0 = performance.now()
                const headerBuf = await file.slice(0, EVTX_HEADER_SIZE).arrayBuffer()
                const meta = parseFileHeader(new Uint8Array(headerBuf))
                const parseTimeMs = Math.round(performance.now() - t0)

                respond({type: "fileMeta", meta, fileName: msg.fileName, parseTimeMs, fileId})

                // Step 2: Compute actual chunk count from file size (header numChunks is a ushort, max 65535)
                const actualChunkCount = Math.floor((file.size - meta.headerBlockSize) / EVTX_CHUNK_SIZE)
                const totalChunks = Math.max(meta.numChunks, actualChunkCount)

                // Step 3: Stream chunks one at a time
                let totalRecords = 0
                for (let i = 0; i < totalChunks; i++) {
                    const chunkBytes = await readChunk(file, i, meta.headerBlockSize)
                    const rawRecords: RecordMeta[] = parseChunkMetadata(chunkBytes, i)
                    for (let j = 0; j < rawRecords.length; j++) {
                        rawRecords[j].recordIndexInChunk = j
                        rawRecords[j].fileId = fileId
                    }
                    totalRecords += rawRecords.length

                    respond({
                        type: "chunkRecords",
                        records: rawRecords,
                        chunkIndex: i,
                        chunksProcessed: i + 1,
                        totalChunks,
                        fileId,
                    })
                }

                respond({type: "streamDone", totalRecords, fileId})
            } catch (err: unknown) {
                const message = err instanceof Error ? err.message : String(err)
                respond({type: "error", message: `Stream parse failed: ${message}`})
            }
            break
        }

        case "renderRecord": {
            try {
                const chunkBytes = await readChunk(msg.file, msg.chunkIndex, EVTX_HEADER_SIZE)
                const record = renderRecord(chunkBytes, msg.chunkIndex, msg.recordIndex)
                record.fileId = msg.fileId

                respond({
                    type: "renderedRecord",
                    record,
                    chunkIndex: msg.chunkIndex,
                    recordIndex: msg.recordIndex,
                    fileId: msg.fileId,
                })
            } catch (err: unknown) {
                const message = err instanceof Error ? err.message : String(err)
                respond({type: "error", message: `Render record failed: ${message}`})
            }
            break
        }

        case "renderBatch": {
            try {
                const results: EvtxRecord[] = []
                // Group by chunkIndex to avoid re-reading the same chunk
                const byChunk = new Map<number, number[]>()
                for (let i = 0; i < msg.records.length; i++) {
                    const rec = msg.records[i]
                    let list = byChunk.get(rec.chunkIndex)
                    if (!list) {
                        list = []
                        byChunk.set(rec.chunkIndex, list)
                    }
                    list.push(rec.recordIndex)
                }

                for (const [chunkIndex, recordIndices] of byChunk) {
                    const chunkBytes = await readChunk(msg.file, chunkIndex, EVTX_HEADER_SIZE)
                    for (let j = 0; j < recordIndices.length; j++) {
                        const rec = renderRecord(chunkBytes, chunkIndex, recordIndices[j])
                        rec.fileId = msg.fileId
                        results.push(rec)
                    }
                }

                respond({type: "renderedBatch", records: results, fileId: msg.fileId})
            } catch (err: unknown) {
                const message = err instanceof Error ? err.message : String(err)
                respond({type: "error", message: `Batch render failed: ${message}`})
            }
            break
        }
    }
}
