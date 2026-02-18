import type {EvtxRecord, FileMeta, RecordMeta} from "../types"

/** Messages sent from the main thread to the parse worker. */
export type WorkerRequest =
    | { type: "init"; frameworkUrl: string }
    | { type: "parse"; data: ArrayBuffer; fileName: string }
    | { type: "parseStream"; file: File; fileName: string }
    | { type: "renderRecord"; file: File; chunkIndex: number; recordIndex: number }

/**
 * Messages sent from the parse worker back to the main thread.
 *
 * Full-file flow: meta → preview → progress (×N) → records.
 * Stream flow: fileMeta → chunkRecords (×N) → streamDone.
 * On-demand: renderedRecord.
 */
export type WorkerResponse =
    | { type: "ready" }
    | { type: "meta"; totalRecords: number; numChunks: number; parseTimeMs: number; fileName: string }
    | { type: "preview"; records: EvtxRecord[] }
    | { type: "progress"; loaded: number }
    | { type: "records"; records: EvtxRecord[] }
    | { type: "error"; message: string }
    | { type: "fileMeta"; meta: FileMeta; fileName: string; parseTimeMs: number }
    | { type: "chunkRecords"; records: RecordMeta[]; chunkIndex: number; chunksProcessed: number; totalChunks: number }
    | { type: "streamDone"; totalRecords: number }
    | { type: "renderedRecord"; record: EvtxRecord; chunkIndex: number; recordIndex: number }
