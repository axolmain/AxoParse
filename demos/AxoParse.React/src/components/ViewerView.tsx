import {useCallback, useMemo, useState} from 'react'
import {Stack, Text} from '@mantine/core'
import {SortingState, VisibilityState} from '@tanstack/react-table'
import type {EvtxRecord, FileSession, RecordMeta, StreamProgress} from '../types'
import {EMPTY_FILTERS, FilterState} from '../lib/filter-types'
import {deriveStorageKey} from '../lib/bookmarks'
import {useStreamProgress} from '../hooks/useStreamProgress'
import {useFilteredRecords} from '../hooks/useFilteredRecords'
import {useDashboardStats} from '../hooks/useDashboardStats'
import {useBookmarks} from '../hooks/useBookmarks'
import {StatsBar} from './StatsBar'
import {FilterBar} from './FilterBar'
import {ColumnVisibilityMenu} from './ColumnVisibilityMenu'
import {EventTable} from './EventTable'
import {DashboardPanel} from './dashboard/DashboardPanel'
import {ExportDialog} from './ExportDialog'
import {BookmarksPanel} from './BookmarksPanel'

const TABLE_COLUMNS = [
    {id: 'timestamp', header: 'Timestamp'},
    {id: 'eventId', header: 'Event ID'},
    {id: 'level', header: 'Level'},
    {id: 'provider', header: 'Provider'},
    {id: 'computer', header: 'Computer'},
    {id: 'channel', header: 'Channel'},
]

function getEventId(r: RecordMeta): string {
    return r.eventId
}

function getProvider(r: RecordMeta): string {
    return r.provider
}

function getComputer(r: RecordMeta): string {
    return r.computer
}

function getChannel(r: RecordMeta): string {
    return r.channel
}

function compareNumeric(a: string, b: string): number {
    return Number(a) - Number(b)
}

/** Extracts sorted unique values from records by a given accessor. */
function useUniqueValues(
    records: RecordMeta[],
    accessor: (r: RecordMeta) => string,
    compareFn?: (a: string, b: string) => number,
): string[] {
    return useMemo(() => {
        const set = new Set<string>()
        for (let i = 0; i < records.length; i++) set.add(accessor(records[i]))
        return Array.from(set).sort(compareFn)
    }, [records, accessor, compareFn])
}

export interface ViewerViewProps {
    allRecords: RecordMeta[]
    anyStreaming: boolean
    streamProgress: StreamProgress
    files: FileSession[]
    error: string | null
    requestRecordRender: (fileId: string, file: File, chunkIndex: number, recordIndex: number) => Promise<EvtxRecord>
    requestBatchRender: (fileId: string, file: File, records: RecordMeta[]) => Promise<EvtxRecord[]>
}

export function ViewerView({
                               allRecords,
                               anyStreaming,
                               streamProgress,
                               files,
                               error,
                               requestRecordRender,
                               requestBatchRender,
                           }: ViewerViewProps) {
    const [sorting, setSorting] = useState<SortingState>([])
    const [columnVisibility, setColumnVisibility] = useState<VisibilityState>({})
    const [filters, setFilters] = useState<FilterState>(EMPTY_FILTERS)
    const [showDashboard, setShowDashboard] = useState(true)
    const [zoomDomain, setZoomDomain] = useState<[number, number] | null>(null)
    const [exportOpen, setExportOpen] = useState(false)
    const [bookmarksPanelOpen, setBookmarksPanelOpen] = useState(false)

    // Bookmarks
    const bookmarkStorageKey = useMemo(
        () => deriveStorageKey(files.map((f) => f.fileName)),
        [files],
    )
    const {
        bookmarks,
        bookmarkKeys,
        toggle: toggleBookmark,
        setNote: setBookmarkNote,
        remove: removeBookmark,
        clear: clearBookmarks
    } = useBookmarks(bookmarkStorageKey)

    useStreamProgress(anyStreaming, streamProgress.chunksProcessed, streamProgress.totalChunks)

    const filteredRecords = useFilteredRecords(allRecords, filters, bookmarkKeys)
    const dashboardStats = useDashboardStats(filteredRecords, zoomDomain)

    const availableEventIds = useUniqueValues(allRecords, getEventId, compareNumeric)
    const availableProviders = useUniqueValues(allRecords, getProvider)
    const availableComputers = useUniqueValues(allRecords, getComputer)
    const availableChannels = useUniqueValues(allRecords, getChannel)

    const fileMap = useMemo(() => {
        const m = new Map<string, File>()
        for (const s of files) m.set(s.fileId, s.file)
        return m
    }, [files])

    const availableFiles = useMemo(() => {
        return files.map((s) => ({value: s.fileId, label: s.fileName}))
    }, [files])

    const aggregatedStats = useMemo(() => {
        let totalRecords = 0
        let numChunks = 0
        let parseTimeMs = 0
        const fileNames: string[] = []
        for (const s of files) {
            if (s.stats) {
                totalRecords += s.stats.totalRecords
                numChunks += s.stats.numChunks
                parseTimeMs = Math.max(parseTimeMs, s.stats.parseTimeMs)
                fileNames.push(s.stats.fileName)
            }
        }
        return {
            totalRecords,
            numChunks,
            parseTimeMs,
            fileName: fileNames.length <= 1 ? (fileNames[0] ?? null) : `${fileNames.length} files`,
        }
    }, [files])

    const handleTimeRangeSelect = useCallback((start: Date, end: Date) => {
        setFilters((prev) => ({...prev, timeRange: [start, end]}))
    }, [])

    const handleRecordRender = useCallback((file: File, chunkIndex: number, recordIndex: number, fileId: string) => {
        return requestRecordRender(fileId, file, chunkIndex, recordIndex)
    }, [requestRecordRender])

    const handleBatchRender = useCallback((_file: File, records: RecordMeta[]) => {
        const byFile = new Map<string, RecordMeta[]>()
        for (const r of records) {
            let list = byFile.get(r.fileId)
            if (!list) {
                list = []
                byFile.set(r.fileId, list)
            }
            list.push(r)
        }

        const promises: Promise<EvtxRecord[]>[] = []
        for (const [fid, recs] of byFile) {
            const f = fileMap.get(fid)
            if (f) promises.push(requestBatchRender(fid, f, recs))
        }

        return Promise.all(promises).then((results) => results.flat())
    }, [fileMap, requestBatchRender])

    const firstFile = files[0]?.file ?? new File([], '')

    return (
        <Stack gap="sm" style={{flex: 1, minHeight: 0}}>
            {error && <Text c="red" size="sm">{error}</Text>}

            <StatsBar
                fileName={aggregatedStats.fileName}
                totalRecords={aggregatedStats.totalRecords}
                numChunks={aggregatedStats.numChunks}
                parseTimeMs={aggregatedStats.parseTimeMs}
                recordCount={allRecords.length}
                filteredCount={filteredRecords.length}
                streaming={anyStreaming}
                chunksProcessed={streamProgress.chunksProcessed}
                totalChunks={streamProgress.totalChunks}
                showDashboard={showDashboard}
                onToggleDashboard={() => setShowDashboard((v) => !v)}
                onExport={() => setExportOpen(true)}
                files={files}
                bookmarkCount={bookmarks.size}
                onOpenBookmarks={() => setBookmarksPanelOpen(true)}
                trailing={
                    <ColumnVisibilityMenu
                        columns={TABLE_COLUMNS}
                        visibility={columnVisibility}
                        onVisibilityChange={setColumnVisibility}
                    />
                }
            />

            {showDashboard && !anyStreaming && filteredRecords.length > 0 && (
                <DashboardPanel
                    stats={dashboardStats}
                    onTimeRangeSelect={handleTimeRangeSelect}
                    zoomDomain={zoomDomain}
                    onZoomChange={setZoomDomain}
                    activeTimeRange={filters.timeRange}
                />
            )}

            <FilterBar
                filters={filters}
                onChange={setFilters}
                availableEventIds={availableEventIds}
                availableProviders={availableProviders}
                availableChannels={availableChannels}
                availableFiles={availableFiles}
            />

            <EventTable
                records={filteredRecords}
                fileMap={fileMap}
                requestRecordRender={handleRecordRender}
                sorting={sorting}
                onSortingChange={setSorting}
                columnVisibility={columnVisibility}
                onColumnVisibilityChange={setColumnVisibility}
                filters={filters}
                onFiltersChange={setFilters}
                availableEventIds={availableEventIds}
                availableProviders={availableProviders}
                availableChannels={availableChannels}
                availableComputers={availableComputers}
                availableFiles={availableFiles}
                showFileColumn={files.length > 1}
                files={files}
                bookmarkKeys={bookmarkKeys}
                onToggleBookmark={toggleBookmark}
            />
            <ExportDialog
                opened={exportOpen}
                onClose={() => setExportOpen(false)}
                filteredRecords={filteredRecords}
                file={firstFile}
                fileName={aggregatedStats.fileName ?? 'export'}
                requestBatchRender={handleBatchRender}
            />
            <BookmarksPanel
                opened={bookmarksPanelOpen}
                onClose={() => setBookmarksPanelOpen(false)}
                bookmarks={bookmarks}
                onSetNote={setBookmarkNote}
                onRemove={removeBookmark}
                onClear={clearBookmarks}
            />
        </Stack>
    )
}
