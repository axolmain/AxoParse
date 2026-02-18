import type {EvtxRecord} from "./types"
import {formatXml} from "./format-xml"

type ExportFormat = "csv" | "json" | "xml"

/** Fields included in CSV and JSON exports (excludes raw xml and chunkIndex). */
const EXPORT_FIELDS: (keyof EvtxRecord)[] = [
    "recordId", "timestamp", "eventId", "levelText", "provider", "computer",
    "channel", "task", "opcode", "keywords", "version", "processId",
    "threadId", "securityUserId", "activityId", "relatedActivityId", "eventData",
]

/**
 * Export records to the given format and trigger a browser download.
 *
 * @param records - The records to export (filtered/sorted from table).
 * @param format  - Target format: csv, json, or xml.
 * @param fileName - Base file name without extension.
 */
export function exportRecords(records: EvtxRecord[], format: ExportFormat, fileName: string): void {
    let content: string
    let mime: string
    let ext: string

    switch (format) {
        case "csv":
            content = toCsv(records)
            mime = "text/csv;charset=utf-8"
            ext = "csv"
            break
        case "json":
            content = toJson(records)
            mime = "application/json;charset=utf-8"
            ext = "json"
            break
        case "xml":
            content = toXml(records)
            mime = "application/xml;charset=utf-8"
            ext = "xml"
            break
    }

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

function toCsv(records: EvtxRecord[]): string {
    const header = EXPORT_FIELDS.map(csvEscape).join(",")
    const lines: string[] = [header]
    for (let i = 0; i < records.length; i++) {
        const rec = records[i]
        const row = EXPORT_FIELDS.map((f) => csvEscape(rec[f])).join(",")
        lines.push(row)
    }
    return lines.join("\n")
}

function toJson(records: EvtxRecord[]): string {
    const slim = records.map((rec) => {
        const obj: Record<string, string | number> = {}
        for (const f of EXPORT_FIELDS) {
            obj[f] = rec[f]
        }
        return obj
    })
    return JSON.stringify(slim, null, 2)
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
