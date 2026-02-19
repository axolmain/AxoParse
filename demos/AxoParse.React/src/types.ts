/** A single parsed EVTX event record with all extracted fields. */
export interface EvtxRecord {
    recordId: number
    timestamp: string
    xml: string
    chunkIndex: number
    eventId: string
    provider: string
    level: number
    levelText: string
    computer: string
    channel: string
    task: string
    opcode: string
    keywords: string
    version: string
    processId: string
    threadId: string
    securityUserId: string
    activityId: string
    relatedActivityId: string
    eventData: string
    fileId: string
}

/** Metadata returned by {@link parseEvtxFile} after the initial parse. */
export interface ParseMeta {
    totalRecords: number
    numChunks: number
}

/** Metadata extracted from the 4096-byte EVTX file header. */
export interface FileMeta {
    numChunks: number
    majorVersion: number
    minorVersion: number
    headerBlockSize: number
    isDirty: boolean
    isFull: boolean
}

/** Lightweight per-record metadata returned by chunk-at-a-time parsing. */
export interface RecordMeta {
    recordId: number
    timestamp: string
    chunkIndex: number
    recordIndexInChunk: number
    eventId: string
    provider: string
    level: number
    levelText: string
    computer: string
    channel: string
    fileId: string
}

/** Summary statistics for a parsed EVTX file. */
export interface Stats {
    totalRecords: number
    numChunks: number
    parseTimeMs: number
    fileName: string
}

/** Progress of chunk-at-a-time streaming. */
export interface StreamProgress {
    chunksProcessed: number
    totalChunks: number
}

/** State for a single file being parsed/streamed. */
export interface FileSession {
    fileId: string
    file: File
    fileName: string
    stats: Stats | null
    streaming: boolean
    streamProgress: StreamProgress
}
