import type {EvtxRecord} from "./types"
import {formatXml} from "./format-xml"

export type ExportFormat = "csv" | "json" | "jsonl" | "xml"

/** Fields included in CSV, JSON, and JSONL exports (excludes raw xml and chunkIndex). */
export const EXPORT_FIELDS: (keyof EvtxRecord)[] = [
    "recordId", "timestamp", "eventId", "levelText", "provider", "computer",
    "channel", "task", "opcode", "keywords", "version", "processId",
    "threadId", "securityUserId", "activityId", "relatedActivityId", "eventData",
]

export interface ExportOptions {
    records: EvtxRecord[]
    format: ExportFormat
    fileName: string
    fields?: (keyof EvtxRecord)[]
    onProgress?: (fraction: number) => void
    signal?: AbortSignal
}

/**
 * Export records to the given format and trigger a browser download.
 * For large exports (>5000 records), yields to the event loop between chunks.
 */
export async function exportRecords(options: ExportOptions): Promise<void> {
    const {records, format, fileName, fields, onProgress, signal} = options
    let content: string
    let mime: string
    let ext: string

    switch (format) {
        case "csv":
            content = await toCsvAsync(records, fields ?? EXPORT_FIELDS, onProgress, signal)
            mime = "text/csv;charset=utf-8"
            ext = "csv"
            break
        case "json":
            content = toJson(records, fields ?? EXPORT_FIELDS)
            onProgress?.(1)
            mime = "application/json;charset=utf-8"
            ext = "json"
            break
        case "jsonl":
            content = await toJsonlAsync(records, fields ?? EXPORT_FIELDS, onProgress, signal)
            mime = "application/x-ndjson;charset=utf-8"
            ext = "jsonl"
            break
        case "xml":
            content = toXml(records)
            onProgress?.(1)
            mime = "application/xml;charset=utf-8"
            ext = "xml"
            break
    }

    if (signal?.aborted) return

    const blob = new Blob([content], {type: mime})
    const url = URL.createObjectURL(blob)
    const a = document.createElement("a")
    a.href = url
    a.download = `${fileName}.${ext}`
    a.click()
    URL.revokeObjectURL(url)
}

/** Escape a value for CSV: wrap in quotes if it contains commas, quotes, or newlines. */
function csvEscape(value: string | number): string {
    const str = String(value)
    if (str.includes(",") || str.includes('"') || str.includes("\n") || str.includes("\r")) {
        return `"${str.replace(/"/g, '""')}"`
    }
    return str
}

const CHUNK_SIZE = 1000

async function toCsvAsync(
    records: EvtxRecord[],
    fields: (keyof EvtxRecord)[],
    onProgress?: (fraction: number) => void,
    signal?: AbortSignal,
): Promise<string> {
    const header = fields.map(csvEscape).join(",")
    const lines: string[] = [header]

    for (let i = 0; i < records.length; i++) {
        if (signal?.aborted) return ""
        const rec = records[i]
        const row = fields.map((f) => csvEscape(rec[f])).join(",")
        lines.push(row)

        if (i > 0 && i % CHUNK_SIZE === 0) {
            onProgress?.(i / records.length)
            await yieldToEventLoop()
        }
    }
    onProgress?.(1)
    return lines.join("\n")
}

function toJson(records: EvtxRecord[], fields: (keyof EvtxRecord)[]): string {
    const slim = records.map((rec) => {
        const obj: Record<string, string | number> = {}
        for (const f of fields) {
            obj[f] = rec[f]
        }
        return obj
    })
    return JSON.stringify(slim, null, 2)
}

async function toJsonlAsync(
    records: EvtxRecord[],
    fields: (keyof EvtxRecord)[],
    onProgress?: (fraction: number) => void,
    signal?: AbortSignal,
): Promise<string> {
    const lines: string[] = []

    for (let i = 0; i < records.length; i++) {
        if (signal?.aborted) return ""
        const rec = records[i]
        const obj: Record<string, string | number> = {}
        for (const f of fields) {
            obj[f] = rec[f]
        }
        lines.push(JSON.stringify(obj))

        if (i > 0 && i % CHUNK_SIZE === 0) {
            onProgress?.(i / records.length)
            await yieldToEventLoop()
        }
    }
    onProgress?.(1)
    return lines.join("\n")
}

/** Uses the original parsed XML stored on each record, pretty-formatted. */
function toXml(records: EvtxRecord[]): string {
    const parts: string[] = ['<?xml version="1.0" encoding="utf-8"?>', "<Events>"]
    for (let i = 0; i < records.length; i++) {
        parts.push(formatXml(records[i].xml, "    "))
    }
    parts.push("</Events>")
    return parts.join("\n")
}

function yieldToEventLoop(): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, 0))
}
