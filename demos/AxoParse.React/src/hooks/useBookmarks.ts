import {useCallback, useEffect, useMemo, useRef, useState} from 'react'
import type {RecordMeta} from '../types'
import {
    Bookmark,
    loadBookmarks,
    makeBookmarkKey,
    saveBookmarks,
    setBookmarkNote,
    toggleBookmark,
} from '../lib/bookmarks'

export interface UseBookmarksReturn {
    bookmarks: Map<string, Bookmark>
    bookmarkKeys: Set<string>
    toggle: (record: RecordMeta) => void
    setNote: (key: string, note: string) => void
    remove: (key: string) => void
    clear: () => void
    isBookmarked: (record: RecordMeta) => boolean
}

export function useBookmarks(storageKey: string): UseBookmarksReturn {
    const [bookmarks, setBookmarks] = useState<Map<string, Bookmark>>(() => loadBookmarks(storageKey))
    const isInitialMount = useRef(true)

    useEffect(() => {
        setBookmarks(loadBookmarks(storageKey))
        isInitialMount.current = true
    }, [storageKey])

    useEffect(() => {
        if (isInitialMount.current) {
            isInitialMount.current = false
            return
        }
        saveBookmarks(storageKey, bookmarks)
    }, [storageKey, bookmarks])

    const toggle = useCallback((record: RecordMeta) => {
        setBookmarks((prev) => toggleBookmark(prev, record))
    }, [])

    const setNote = useCallback((key: string, note: string) => {
        setBookmarks((prev) => setBookmarkNote(prev, key, note))
    }, [])

    const remove = useCallback((key: string) => {
        setBookmarks((prev) => {
            const next = new Map(prev)
            next.delete(key)
            return next
        })
    }, [])

    const clear = useCallback(() => {
        setBookmarks(new Map())
    }, [])

    const isBookmarked = useCallback((record: RecordMeta): boolean => {
        return bookmarks.has(makeBookmarkKey(record))
    }, [bookmarks])

    const bookmarkKeys = useMemo(() => new Set(bookmarks.keys()), [bookmarks])

    return {bookmarks, bookmarkKeys, toggle, setNote, remove, clear, isBookmarked}
}
