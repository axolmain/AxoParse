import {useCallback, useMemo, useRef, useState} from 'react'
import {Badge} from '@mantine/core'
import {
    ColumnDef,
    createColumnHelper,
    flexRender,
    getCoreRowModel,
    getSortedRowModel,
    SortingState,
    useReactTable,
    VisibilityState,
} from '@tanstack/react-table'
import {useVirtualizer} from '@tanstack/react-virtual'
import type {EvtxRecord, FileSession, RecordMeta} from '../types'
import {LEVEL_CONFIG} from '../lib/level-colors'
import {RowDetail} from './RowDetail'
import classes from './EventTable.module.css'

const ROW_HEIGHT = 36
const EXPANDED_HEIGHT = 320

const SORT_INDICATORS: Record<string, string> = {asc: ' \u2191', desc: ' \u2193'}

/** Join CSS module class names, filtering out falsy values. */
function cx(...names: (string | false | undefined | null)[]): string {
    let result = ''
    for (let i = 0; i < names.length; i++) {
        if (names[i]) result += (result ? ' ' : '') + names[i]
    }
    return result
}

const columnHelper = createColumnHelper<RecordMeta>()

const baseColumns: ColumnDef<RecordMeta, any>[] = [
    columnHelper.accessor('timestamp', {
        header: 'Timestamp',
        size: 190,
        cell: (info) => {
            const value = info.getValue()
            if (!value) return ''
            return value.replace('T', ' ').replace(/\.\d+Z$/, '')
        },
    }),
    columnHelper.accessor('eventId', {header: 'Event ID', size: 80}),
    columnHelper.accessor('level', {
        header: 'Level',
        size: 110,
        cell: (info) => {
            const level = info.getValue()
            const config = LEVEL_CONFIG[level]
            return (
                <Badge size="xs" variant="light" color={config?.color ?? 'gray'}>
                    {config?.label ?? `Level ${level}`}
                </Badge>
            )
        },
    }),
    columnHelper.accessor('provider', {header: 'Provider', size: 220}),
    columnHelper.accessor('computer', {header: 'Computer', size: 160}),
    columnHelper.accessor('channel', {header: 'Channel', size: 140}),
]

interface EventTableProps {
    records: RecordMeta[]
    fileMap: Map<string, File>
    requestRecordRender: (file: File, chunkIndex: number, recordIndex: number, fileId: string) => Promise<EvtxRecord>
    sorting: SortingState
    onSortingChange: (updater: SortingState | ((prev: SortingState) => SortingState)) => void
    columnVisibility: VisibilityState
    onColumnVisibilityChange: (updater: VisibilityState | ((prev: VisibilityState) => VisibilityState)) => void
    showFileColumn?: boolean
    files?: FileSession[]
    bookmarkKeys?: Set<string>
    onToggleBookmark?: (record: RecordMeta) => void
}

export function EventTable({
                               records,
                               fileMap,
                               requestRecordRender,
                               sorting,
                               onSortingChange,
                               columnVisibility,
                               onColumnVisibilityChange,
                               showFileColumn,
                               files,
                               bookmarkKeys,
                               onToggleBookmark,
                           }: EventTableProps) {
    const containerRef = useRef<HTMLDivElement>(null)
    const [expandedRows, setExpandedRows] = useState<Set<string>>(new Set())
    const [expandedData, setExpandedData] = useState<Map<string, EvtxRecord | 'loading'>>(new Map())
    const [focusedIndex, setFocusedIndex] = useState<number>(-1)

    // Build fileId→fileName map for the file column
    const fileNameMap = useMemo(() => {
        const m = new Map<string, string>()
        if (files) {
            for (const s of files) m.set(s.fileId, s.fileName)
        }
        return m
    }, [files])

    const columns = useMemo(() => {
        if (!showFileColumn) return baseColumns
        const fileCol = columnHelper.accessor('fileId', {
            header: 'File',
            size: 140,
            cell: (info) => {
                const fid = info.getValue()
                const name = fileNameMap.get(fid)
                if (!name) return fid.slice(0, 8)
                // Show short name (last path component, truncated)
                const short = name.length > 20 ? name.slice(0, 17) + '...' : name
                return short
            },
        })
        return [fileCol, ...baseColumns]
    }, [showFileColumn, fileNameMap])

    const table = useReactTable({
        data: records,
        columns,
        state: {sorting, columnVisibility},
        onSortingChange,
        onColumnVisibilityChange,
        getCoreRowModel: getCoreRowModel(),
        getSortedRowModel: getSortedRowModel(),
    })

    const {rows} = table.getRowModel()

    const toggleExpand = useCallback(async (rowIndex: number) => {
        const row = rows[rowIndex]
        if (!row) return
        const key = `${row.original.fileId}:${row.original.chunkIndex}:${row.original.recordIndexInChunk}`

        setExpandedRows((prev) => {
            const next = new Set(prev)
            if (next.has(key)) {
                next.delete(key)
            } else {
                next.add(key)
                if (!expandedData.has(key)) {
                    setExpandedData((d) => new Map(d).set(key, 'loading'))
                    const file = fileMap.get(row.original.fileId)
                    if (file) {
                        requestRecordRender(file, row.original.chunkIndex, row.original.recordIndexInChunk, row.original.fileId)
                            .then((record) => {
                                setExpandedData((d) => new Map(d).set(key, record))
                            })
                    }
                }
            }
            return next
        })
    }, [rows, fileMap, requestRecordRender, expandedData])

    const getRowHeight = useCallback((index: number) => {
        const row = rows[index]
        if (!row) return ROW_HEIGHT
        const key = `${row.original.fileId}:${row.original.chunkIndex}:${row.original.recordIndexInChunk}`
        return expandedRows.has(key) ? ROW_HEIGHT + EXPANDED_HEIGHT : ROW_HEIGHT
    }, [rows, expandedRows])

    const virtualizer = useVirtualizer({
        count: rows.length,
        getScrollElement: () => containerRef.current,
        estimateSize: getRowHeight,
        overscan: 20,
    })

    const handleKeyDown = useCallback((e: React.KeyboardEvent) => {
        if (e.key === 'ArrowDown') {
            e.preventDefault()
            setFocusedIndex((prev) => Math.min(prev + 1, rows.length - 1))
        } else if (e.key === 'ArrowUp') {
            e.preventDefault()
            setFocusedIndex((prev) => Math.max(prev - 1, 0))
        } else if (e.key === 'Enter' && focusedIndex >= 0) {
            e.preventDefault()
            toggleExpand(focusedIndex)
        } else if (e.key === 'Escape' && focusedIndex >= 0) {
            e.preventDefault()
            const row = rows[focusedIndex]
            if (row) {
                const key = `${row.original.fileId}:${row.original.chunkIndex}:${row.original.recordIndexInChunk}`
                if (expandedRows.has(key)) {
                    setExpandedRows((prev) => {
                        const next = new Set(prev)
                        next.delete(key)
                        return next
                    })
                }
            }
        }
    }, [focusedIndex, rows, expandedRows, toggleExpand])

    const headerGroups = table.getHeaderGroups()

    return (
        <div
            ref={containerRef}
            className={classes.container}
            tabIndex={0}
            onKeyDown={handleKeyDown}
        >
            <div className={classes.header}>
                {headerGroups.map((headerGroup) => (
                    <div key={headerGroup.id} className={classes.headerRow}>
                        {onToggleBookmark && (
                            <div className={classes.headerCell} style={{width: 36, flex: '0 0 36px'}}>
                                {'\u2606'}
                            </div>
                        )}
                        {headerGroup.headers.map((header) => (
                            <div
                                key={header.id}
                                className={cx(classes.headerCell, header.column.getCanSort() && classes.sortable)}
                                style={{width: header.getSize(), flex: `0 0 ${header.getSize()}px`}}
                                onClick={header.column.getToggleSortingHandler()}
                            >
                                {header.isPlaceholder
                                    ? null
                                    : flexRender(header.column.columnDef.header, header.getContext())}
                                {SORT_INDICATORS[header.column.getIsSorted() as string] ?? ''}
                            </div>
                        ))}
                    </div>
                ))}
            </div>

            <div style={{height: virtualizer.getTotalSize(), position: 'relative'}}>
                {virtualizer.getVirtualItems().map((virtualRow) => {
                    const row = rows[virtualRow.index]
                    const key = `${row.original.fileId}:${row.original.chunkIndex}:${row.original.recordIndexInChunk}`
                    const isExpanded = expandedRows.has(key)
                    const isFocused = virtualRow.index === focusedIndex

                    return (
                        <div
                            key={row.id}
                            className={cx(classes.row, isFocused && classes.focused, isExpanded && classes.expanded)}
                            style={{
                                position: 'absolute',
                                top: 0,
                                transform: `translateY(${virtualRow.start}px)`,
                                width: '100%',
                            }}
                        >
                            <div
                                className={classes.rowCells}
                                onClick={() => {
                                    setFocusedIndex(virtualRow.index)
                                    toggleExpand(virtualRow.index)
                                }}
                            >
                                {onToggleBookmark && (
                                    <div
                                        className={classes.cell}
                                        style={{flex: '0 0 36px', cursor: 'pointer', textAlign: 'center', fontSize: 14}}
                                        onClick={(e) => {
                                            e.stopPropagation()
                                            onToggleBookmark(row.original)
                                        }}
                                    >
                                        {bookmarkKeys?.has(key) ? '\u2605' : '\u2606'}
                                    </div>
                                )}
                                {row.getVisibleCells().map((cell) => (
                                    <div
                                        key={cell.id}
                                        className={classes.cell}
                                        style={{flex: `0 0 ${cell.column.getSize()}px`}}
                                    >
                                        {flexRender(cell.column.columnDef.cell, cell.getContext())}
                                    </div>
                                ))}
                            </div>
                            {isExpanded && (
                                <div className={classes.detailPanel}>
                                    <RowDetail record={expandedData.get(key) ?? 'loading'}/>
                                </div>
                            )}
                        </div>
                    )
                })}
            </div>
        </div>
    )
}
