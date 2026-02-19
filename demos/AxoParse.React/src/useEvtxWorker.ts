import {useCallback, useEffect, useRef, useState} from "react"
import type {EvtxRecord, FileSession, RecordMeta, StreamProgress} from "./types"
import type {WorkerRequest, WorkerResponse} from "./worker/messages"

/**
 * Fully-qualified URL to the WASM framework directory.
 * Workers resolve dynamic imports relative to their own blob URL,
 * not the page origin, so an absolute URL is required.
 * Uses Vite's BASE_URL so the path works on both localhost and GitHub Pages.
 */
const WASM_FRAMEWORK_URL = `${window.location.origin}${import.meta.env.BASE_URL}wasm/_framework`

/**
 * Minimum ms between React state flushes during streaming.
 * Scales up with record count to avoid O(N) table rebuilds at high frequency.
 */
const FLUSH_MIN_MS = 250
const FLUSH_MAX_MS = 2000

export interface EvtxWorkerState {
    wasmReady: boolean
    wasmLoading: boolean
    error: string | null
    records: EvtxRecord[]
    parsing: boolean
    loadProgress: number
    parse: (file: File) => void

    /** All stream records merged across files. */
    allRecords: RecordMeta[]
    /** True if any file is still streaming. */
    anyStreaming: boolean
    /** Combined stream progress (summed across files). */
    streamProgress: StreamProgress
    /** All active file sessions. */
    files: FileSession[]

    addFile: (file: File) => void
    removeFile: (fileId: string) => void
    requestRecordRender: (fileId: string, file: File, chunkIndex: number, recordIndex: number) => Promise<EvtxRecord>
    requestBatchRender: (fileId: string, file: File, records: RecordMeta[]) => Promise<EvtxRecord[]>
}

/**
 * React hook that manages a Web Worker for off-main-thread EVTX parsing.
 * Supports multi-file concurrent streaming with merged record output.
 *
 * Streaming records accumulate in mutable refs per-file and flush to React state
 * at most every {@link FLUSH_INTERVAL_MS} to avoid O(n²) array copies.
 */
export function useEvtxWorker(): EvtxWorkerState {
    const [wasmReady, setWasmReady] = useState(false)
    const [wasmLoading, setWasmLoading] = useState(true)
    const [error, setError] = useState<string | null>(null)
    const [records, setRecords] = useState<EvtxRecord[]>([])
    const [parsing, setParsing] = useState(false)
    const [loadProgress, setLoadProgress] = useState(0)
    const [allRecords, setAllRecords] = useState<RecordMeta[]>([])
    const [filesState, setFilesState] = useState<FileSession[]>([])
    const [streamProgress, setStreamProgress] = useState<StreamProgress>({chunksProcessed: 0, totalChunks: 0})

    const workerRef = useRef<Worker | null>(null)
    const renderedRecordCache = useRef<Map<string, EvtxRecord>>(new Map())
    const pendingRenders = useRef<Map<string, { resolve: (r: EvtxRecord) => void }>>(new Map())
    const pendingBatchRenders = useRef<Map<string, { resolve: (records: EvtxRecord[]) => void }>>(new Map())

    // Per-file mutable accumulators
    const streamAccumulators = useRef<Map<string, RecordMeta[]>>(new Map())
    const sessionsRef = useRef<Map<string, FileSession>>(new Map())
    const lastFlushTime = useRef<number>(0)
    const flushTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
    const streamDirty = useRef(false)

    // Append-only merged array — avoids rebuilding from scratch every flush
    const mergedRef = useRef<RecordMeta[]>([])
    const flushedPerFile = useRef<Map<string, number>>(new Map())

    const flushStreamRecords = useCallback(() => {
        if (!streamDirty.current) return

        // Append only new records since last flush
        for (const [fileId, acc] of streamAccumulators.current.entries()) {
            const prevLen = flushedPerFile.current.get(fileId) ?? 0
            for (let i = prevLen; i < acc.length; i++) {
                mergedRef.current.push(acc[i])
            }
            flushedPerFile.current.set(fileId, acc.length)
        }

        // Slice to create a new reference for React (native memcpy, much faster than JS loop)
        setAllRecords(mergedRef.current.slice())

        let chunksProcessed = 0
        let totalChunks = 0
        for (const session of sessionsRef.current.values()) {
            chunksProcessed += session.streamProgress.chunksProcessed
            totalChunks += session.streamProgress.totalChunks
        }
        setStreamProgress({chunksProcessed, totalChunks})
        setFilesState(Array.from(sessionsRef.current.values()))

        streamDirty.current = false
        lastFlushTime.current = performance.now()
        if (flushTimer.current !== null) {
            clearTimeout(flushTimer.current)
            flushTimer.current = null
        }
    }, [])

    /** Flush interval scales with record count to keep main thread responsive. */
    const getFlushInterval = useCallback((): number => {
        const n = mergedRef.current.length
        if (n < 20000) return FLUSH_MIN_MS
        if (n < 80000) return 500
        return FLUSH_MAX_MS
    }, [])

    const scheduleFlush = useCallback(() => {
        const now = performance.now()
        const interval = getFlushInterval()
        if (now - lastFlushTime.current >= interval) {
            flushStreamRecords()
        } else if (flushTimer.current === null) {
            const remaining = interval - (now - lastFlushTime.current)
            flushTimer.current = setTimeout(flushStreamRecords, remaining)
        }
    }, [flushStreamRecords, getFlushInterval])

    // Store callbacks in refs so the worker useEffect has a stable dependency array
    const flushRef = useRef(flushStreamRecords)
    flushRef.current = flushStreamRecords
    const scheduleFlushRef = useRef(scheduleFlush)
    scheduleFlushRef.current = scheduleFlush

    useEffect(() => {
        const worker = new Worker(
            new URL("./worker/parse-worker.ts", import.meta.url),
            {type: "module"},
        )
        workerRef.current = worker

        worker.onerror = (e: ErrorEvent) => {
            setError(`Worker error: ${e.message}`)
            setWasmLoading(false)
            setParsing(false)
        }

        worker.onmessage = (e: MessageEvent<WorkerResponse>) => {
            const msg = e.data

            switch (msg.type) {
                case "ready":
                    setWasmReady(true)
                    setWasmLoading(false)
                    break

                case "meta":
                    break

                case "preview":
                    setRecords(msg.records)
                    setLoadProgress(msg.records.length)
                    break

                case "progress":
                    setLoadProgress(msg.loaded)
                    break

                case "records":
                    setRecords(msg.records)
                    setParsing(false)
                    break

                case "fileMeta": {
                    const session = sessionsRef.current.get(msg.fileId)
                    if (session) {
                        session.stats = {
                            totalRecords: 0,
                            numChunks: msg.meta.numChunks,
                            parseTimeMs: msg.parseTimeMs,
                            fileName: msg.fileName,
                        }
                    }
                    break
                }

                case "chunkRecords": {
                    const acc = streamAccumulators.current.get(msg.fileId)
                    if (acc) {
                        const incoming = msg.records
                        for (let i = 0; i < incoming.length; i++) {
                            acc.push(incoming[i])
                        }
                    }

                    const session = sessionsRef.current.get(msg.fileId)
                    if (session) {
                        session.streamProgress = {
                            chunksProcessed: msg.chunksProcessed,
                            totalChunks: msg.totalChunks,
                        }
                    }

                    streamDirty.current = true
                    scheduleFlushRef.current()
                    break
                }

                case "streamDone": {
                    const session = sessionsRef.current.get(msg.fileId)
                    if (session) {
                        session.streaming = false
                        if (session.stats) {
                            session.stats.totalRecords = msg.totalRecords
                        }
                    }
                    streamDirty.current = true
                    flushRef.current()
                    break
                }

                case "renderedRecord": {
                    const key = `${msg.fileId}:${msg.chunkIndex}:${msg.recordIndex}`
                    renderedRecordCache.current.set(key, msg.record)
                    const pending = pendingRenders.current.get(key)
                    if (pending) {
                        pending.resolve(msg.record)
                        pendingRenders.current.delete(key)
                    }
                    break
                }

                case "renderedBatch": {
                    for (let i = 0; i < msg.records.length; i++) {
                        const rec = msg.records[i]
                        const cacheKey = `${rec.fileId}:${rec.chunkIndex}:${rec.recordIndexInChunk}`
                        renderedRecordCache.current.set(cacheKey, rec)
                    }
                    const batchPending = pendingBatchRenders.current.get(msg.batchId)
                    if (batchPending) {
                        batchPending.resolve(msg.records)
                        pendingBatchRenders.current.delete(msg.batchId)
                    }
                    break
                }

                case "error":
                    setError(msg.message)
                    setWasmLoading(false)
                    setParsing(false)
                    break
            }
        }

        const initMsg: WorkerRequest = {type: "init", frameworkUrl: WASM_FRAMEWORK_URL}
        worker.postMessage(initMsg)

        return () => {
            worker.terminate()
            if (flushTimer.current !== null) clearTimeout(flushTimer.current)
        }
    }, [])

    const parse = useCallback((file: File) => {
        if (!wasmReady) {
            setError("WASM module is still loading — please wait")
            return
        }

        setError(null)
        setParsing(true)
        setRecords([])
        setLoadProgress(0)

        const reader = new FileReader()
        reader.onload = () => {
            const buffer = reader.result as ArrayBuffer
            const msg: WorkerRequest = {type: "parse", data: buffer, fileName: file.name}
            workerRef.current!.postMessage(msg, [buffer])
        }
        reader.onerror = () => {
            setError("Failed to read file")
            setParsing(false)
        }
        reader.readAsArrayBuffer(file)
    }, [wasmReady])

    const addFile = useCallback((file: File) => {
        if (!wasmReady) {
            setError("WASM module is still loading — please wait")
            return
        }

        const fileId = crypto.randomUUID()
        const session: FileSession = {
            fileId,
            file,
            fileName: file.name,
            stats: null,
            streaming: true,
            streamProgress: {chunksProcessed: 0, totalChunks: 0},
        }

        sessionsRef.current.set(fileId, session)
        streamAccumulators.current.set(fileId, [])
        setFilesState(Array.from(sessionsRef.current.values()))

        setError(null)

        const msg: WorkerRequest = {type: "parseStream", file, fileName: file.name, fileId}
        workerRef.current!.postMessage(msg)
    }, [wasmReady])

    const removeFile = useCallback((fileId: string) => {
        sessionsRef.current.delete(fileId)
        streamAccumulators.current.delete(fileId)
        flushedPerFile.current.delete(fileId)

        for (const key of renderedRecordCache.current.keys()) {
            if (key.startsWith(fileId + ":")) {
                renderedRecordCache.current.delete(key)
            }
        }

        // Rebuild merged array from scratch since a file was removed
        mergedRef.current = []
        for (const [fid, acc] of streamAccumulators.current.entries()) {
            for (let i = 0; i < acc.length; i++) {
                mergedRef.current.push(acc[i])
            }
            flushedPerFile.current.set(fid, acc.length)
        }

        streamDirty.current = true
        flushStreamRecords()
    }, [flushStreamRecords])

    const requestBatchRender = useCallback((fileId: string, file: File, batchRecords: RecordMeta[]): Promise<EvtxRecord[]> => {
        return new Promise((resolve) => {
            const batchId = crypto.randomUUID()
            pendingBatchRenders.current.set(batchId, {resolve})
            const msg: WorkerRequest = {
                type: "renderBatch",
                file,
                records: batchRecords.map((r) => ({chunkIndex: r.chunkIndex, recordIndex: r.recordIndexInChunk})),
                fileId,
                batchId,
            }
            workerRef.current!.postMessage(msg)
        })
    }, [])

    const requestRecordRender = useCallback((fileId: string, file: File, chunkIndex: number, recordIndex: number): Promise<EvtxRecord> => {
        const key = `${fileId}:${chunkIndex}:${recordIndex}`

        const cached = renderedRecordCache.current.get(key)
        if (cached) return Promise.resolve(cached)

        const existing = pendingRenders.current.get(key)
        if (existing) {
            return new Promise((resolve) => {
                const original = existing.resolve
                existing.resolve = (r: EvtxRecord) => {
                    original(r)
                    resolve(r)
                }
            })
        }

        return new Promise((resolve) => {
            pendingRenders.current.set(key, {resolve})
            const msg: WorkerRequest = {type: "renderRecord", file, chunkIndex, recordIndex, fileId}
            workerRef.current!.postMessage(msg)
        })
    }, [])

    const anyStreaming = filesState.some((s) => s.streaming)

    return {
        wasmReady,
        wasmLoading,
        error,
        records,
        parsing,
        loadProgress,
        parse,
        allRecords,
        anyStreaming,
        streamProgress,
        files: filesState,
        addFile,
        removeFile,
        requestRecordRender,
        requestBatchRender,
    }
}
