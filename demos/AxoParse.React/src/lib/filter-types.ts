export interface FilterState {
    eventIds: string[]
    levels: number[]
    provider: string | null
    channel: string | null
    timeRange: [Date | null, Date | null]
    textSearch: string
    textSearchRegex: boolean
    fileIds: string[]
    bookmarkedOnly: boolean
}

export const EMPTY_FILTERS: FilterState = {
    eventIds: [],
    levels: [],
    provider: null,
    channel: null,
    timeRange: [null, null],
    textSearch: '',
    textSearchRegex: false,
    fileIds: [],
    bookmarkedOnly: false,
}

export function activeFilterCount(filters: FilterState): number {
    let count = 0
    if (filters.eventIds.length > 0) count++
    if (filters.levels.length > 0) count++
    if (filters.provider !== null) count++
    if (filters.channel !== null) count++
    if (filters.timeRange[0] !== null || filters.timeRange[1] !== null) count++
    if (filters.textSearch.length > 0) count++
    if (filters.fileIds.length > 0) count++
    if (filters.bookmarkedOnly) count++
    return count
}
