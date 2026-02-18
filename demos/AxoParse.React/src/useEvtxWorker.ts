import {useCallback, useEffect, useRef, useState} from "react"
import type {EvtxRecord} from "./types"
import type {WorkerRequest, WorkerResponse} from "./worker/messages"

/**
 * Fully-qualified URL to the WASM framework directory.
 * Workers resolve dynamic imports relative to their own blob URL,
 * not the page origin, so an absolute URL is required.
 * Uses Vite's BASE_URL so the path works on both localhost and GitHub Pages.
 */
const WASM_FRAMEWORK_URL = `${window.location.origin}${import.meta.env.BASE_URL}wasm/_framework`

interface Stats {
    totalRecords: number
    numChunks: number
    parseTimeMs: number
    fileName: string
}

interface EvtxWorkerState {
    wasmReady: boolean
    wasmLoading: boolean
    error: string | null
    stats: Stats | null
    records: EvtxRecord[]
    parsing: boolean
    loadProgress: number
    parse: (file: File) => void
}

/**
 * React hook that manages a Web Worker for off-main-thread EVTX parsing.
 * Handles worker lifecycle, WASM initialisation, and message passing.
 *
 * @returns Worker state and a `parse` function to trigger file parsing.
 */
export function useEvtxWorker(): EvtxWorkerState {
    const [wasmReady, setWasmReady] = useState(false)
    const [wasmLoading, setWasmLoading] = useState(true)
    const [error, setError] = useState<string | null>(null)
    const [stats, setStats] = useState<Stats | null>(null)
    const [records, setRecords] = useState<EvtxRecord[]>([])
    const [parsing, setParsing] = useState(false)
    const [loadProgress, setLoadProgress] = useState(0)
    const workerRef = useRef<Worker | null>(null)

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
                    setStats({
                        totalRecords: msg.totalRecords,
                        numChunks: msg.numChunks,
                        parseTimeMs: msg.parseTimeMs,
                        fileName: msg.fileName,
                    })
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
        setStats(null)
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

    return {wasmReady, wasmLoading, error, stats, records, parsing, loadProgress, parse}
}
