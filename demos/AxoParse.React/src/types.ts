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
}

/** Metadata returned by {@link parseEvtxFile} after the initial parse. */
export interface ParseMeta {
    totalRecords: number
    numChunks: number
}
