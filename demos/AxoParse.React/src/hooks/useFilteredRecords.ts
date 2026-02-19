import {useMemo} from 'react'
import type {RecordMeta} from '../types'
import {activeFilterCount, FilterState} from '../lib/filter-types'

export function useFilteredRecords(
    records: RecordMeta[],
    filters: FilterState,
    bookmarks?: Set<string>,
): RecordMeta[] {
    return useMemo(() => {
        if (activeFilterCount(filters) === 0) return records

        let regex: RegExp | null = null
        if (filters.textSearch) {
            if (filters.textSearchRegex) {
                try {
                    regex = new RegExp(filters.textSearch, 'i')
                } catch {
                    regex = null
                }
            } else {
                const escaped = filters.textSearch.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
                regex = new RegExp(escaped, 'i')
            }
        }

        const hasEventIds = filters.eventIds.length > 0
        const eventIdSet = hasEventIds ? new Set(filters.eventIds) : null
        const hasLevels = filters.levels.length > 0
        const levelSet = hasLevels ? new Set(filters.levels) : null
        const hasFileIds = filters.fileIds.length > 0
        const fileIdSet = hasFileIds ? new Set(filters.fileIds) : null
        const startIso = filters.timeRange[0]?.toISOString() ?? null
        const endIso = filters.timeRange[1]?.toISOString() ?? null

        const result: RecordMeta[] = []
        for (let i = 0; i < records.length; i++) {
            const r = records[i]
            if (fileIdSet && !fileIdSet.has(r.fileId)) continue
            if (eventIdSet && !eventIdSet.has(r.eventId)) continue
            if (levelSet && !levelSet.has(r.level)) continue
            if (filters.provider !== null && r.provider !== filters.provider) continue
            if (filters.channel !== null && r.channel !== filters.channel) continue
            if (startIso !== null && r.timestamp < startIso) continue
            if (endIso !== null && r.timestamp > endIso) continue
            if (regex !== null) {
                const hay = `${r.eventId} ${r.provider} ${r.computer} ${r.channel} ${r.levelText} ${r.timestamp}`
                if (!regex.test(hay)) continue
            }
            if (filters.bookmarkedOnly && bookmarks) {
                const key = `${r.fileId}:${r.chunkIndex}:${r.recordIndexInChunk}`
                if (!bookmarks.has(key)) continue
            }
            result.push(r)
        }
        return result
    }, [records, filters, bookmarks])
}
