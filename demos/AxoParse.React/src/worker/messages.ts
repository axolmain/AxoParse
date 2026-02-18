import type {EvtxRecord} from "../types"

/** Messages sent from the main thread to the parse worker. */
export type WorkerRequest =
    | { type: "init"; frameworkUrl: string }
    | { type: "parse"; data: ArrayBuffer; fileName: string }

/**
 * Messages sent from the parse worker back to the main thread.
 *
 * Flow: meta → preview → progress (×N) → records.
 * The preview provides a small initial batch for immediate rendering
 * while the worker continues fetching the remaining records.
 */
export type WorkerResponse =
    | { type: "ready" }
    | { type: "meta"; totalRecords: number; numChunks: number; parseTimeMs: number; fileName: string }
    | { type: "preview"; records: EvtxRecord[] }
    | { type: "progress"; loaded: number }
    | { type: "records"; records: EvtxRecord[] }
    | { type: "error"; message: string }
