import type {RecordMeta} from '../types'

export interface Bookmark {
    key: string
    fileId: string
    chunkIndex: number
    recordIndexInChunk: number
    note: string
    createdAt: number
}

export function makeBookmarkKey(r: RecordMeta): string {
    return `${r.fileId}:${r.chunkIndex}:${r.recordIndexInChunk}`
}

export function loadBookmarks(storageKey: string): Map<string, Bookmark> {
    try {
        const raw = localStorage.getItem(storageKey)
        if (!raw) return new Map()
        const arr: Bookmark[] = JSON.parse(raw)
        const map = new Map<string, Bookmark>()
        for (const b of arr) map.set(b.key, b)
        return map
    } catch {
        return new Map()
    }
}

export function saveBookmarks(storageKey: string, bookmarks: Map<string, Bookmark>): void {
    localStorage.setItem(storageKey, JSON.stringify(Array.from(bookmarks.values())))
}

export function toggleBookmark(bookmarks: Map<string, Bookmark>, record: RecordMeta): Map<string, Bookmark> {
    const key = makeBookmarkKey(record)
    const next = new Map(bookmarks)
    if (next.has(key)) {
        next.delete(key)
    } else {
        next.set(key, {
            key,
            fileId: record.fileId,
            chunkIndex: record.chunkIndex,
            recordIndexInChunk: record.recordIndexInChunk,
            note: '',
            createdAt: Date.now(),
        })
    }
    return next
}

export function setBookmarkNote(bookmarks: Map<string, Bookmark>, key: string, note: string): Map<string, Bookmark> {
    const next = new Map(bookmarks)
    const existing = next.get(key)
    if (existing) {
        next.set(key, {...existing, note})
    }
    return next
}

export function deriveStorageKey(fileNames: string[]): string {
    const sorted = [...fileNames].sort()
    let hash = 0
    const combined = sorted.join('|')
    for (let i = 0; i < combined.length; i++) {
        hash = ((hash << 5) - hash + combined.charCodeAt(i)) | 0
    }
    return `axoparse:bookmarks:${hash}`
}
