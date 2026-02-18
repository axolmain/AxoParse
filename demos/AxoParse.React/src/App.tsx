import {useCallback, useMemo, useRef, useState} from "react"
import {
    createColumnHelper,
    flexRender,
    getCoreRowModel,
    getFilteredRowModel,
    getSortedRowModel,
    type SortingState,
    useReactTable,
} from "@tanstack/react-table"
import {useVirtualizer} from "@tanstack/react-virtual"
import {useEvtxWorker} from "./useEvtxWorker"
import type {EvtxRecord} from "./types"
import {exportRecords} from "./export"
import {formatXml} from "./format-xml"

const ROW_HEIGHT = 29

const columnHelper = createColumnHelper<EvtxRecord>()

const columns = [
    columnHelper.accessor("recordId", {header: "Record ID", size: 90}),
    columnHelper.accessor("timestamp", {
        header: "Timestamp",
        size: 200,
        cell: (info) => {
            const value = info.getValue()
            if (!value) return ""
            return value.replace("T", " ").replace(/\.\d+Z$/, "")
        },
    }),
    columnHelper.accessor("eventId", {header: "Event ID", size: 70}),
    columnHelper.accessor("levelText", {header: "Level", size: 90}),
    columnHelper.accessor("provider", {header: "Provider", size: 200}),
    columnHelper.accessor("computer", {header: "Computer", size: 140}),
    columnHelper.accessor("channel", {header: "Channel", size: 140}),
    columnHelper.accessor("eventData", {
        header: "Event Data",
        size: 360,
        cell: (info) => (
            <div
                className="cell-eventdata clickable"
                onClick={() => (info.table.options.meta as {
                    onDetail: (r: EvtxRecord) => void
                }).onDetail(info.row.original)}
            >
                {info.getValue()}
            </div>
        ),
    }),
]

export default function App() {
    const {wasmReady, wasmLoading, error, stats, records, parsing, loadProgress, parse} = useEvtxWorker()
    const [globalFilter, setGlobalFilter] = useState("")
    const [sorting, setSorting] = useState<SortingState>([])
    const [detailRecord, setDetailRecord] = useState<EvtxRecord | null>(null)
    const tableContainerRef = useRef<HTMLDivElement>(null)

    const handleFile = useCallback(
        (file: File) => {
            setGlobalFilter("")
            setSorting([])
            parse(file)
        },
        [parse],
    )

    const onFileChange = useCallback(
        (e: React.ChangeEvent<HTMLInputElement>) => {
            const file = e.target.files?.[0]
            if (file) handleFile(file)
        },
        [handleFile],
    )

    const tableMeta = useMemo(() => ({onDetail: setDetailRecord}), [])

    const table = useReactTable({
        data: records,
        columns,
        state: {sorting, globalFilter},
        onSortingChange: setSorting,
        onGlobalFilterChange: setGlobalFilter,
        getCoreRowModel: getCoreRowModel(),
        getSortedRowModel: getSortedRowModel(),
        getFilteredRowModel: getFilteredRowModel(),
        meta: tableMeta,
    })

    const {rows} = table.getRowModel()

    const virtualizer = useVirtualizer({
        count: rows.length,
        getScrollElement: () => tableContainerRef.current,
        estimateSize: () => ROW_HEIGHT,
        overscan: 20,
    })

    const exportFileName = useMemo(() => {
        if (!stats) return "export"
        return stats.fileName.replace(/\.evtx$/i, "")
    }, [stats])

    const handleExport = useCallback((format: "csv" | "json" | "xml") => {
        const data = rows.map((row) => row.original)
        exportRecords(data, format, exportFileName)
    }, [rows, exportFileName])

    const sortIndicator = useCallback((sorted: false | "asc" | "desc") => {
        if (sorted === "asc") return " ↑"
        if (sorted === "desc") return " ↓"
        return ""
    }, [])

    return (
        <div className="app">
            <h1>AxoParse — EVTX Viewer</h1>

            <div className="upload-area">
                <p>
                    {wasmLoading
                        ? "Loading WASM module…"
                        : wasmReady
                            ? "Select an .evtx file to parse"
                            : "WASM module failed to load"}
                </p>
                <input
                    type="file"
                    accept=".evtx"
                    onChange={onFileChange}
                    disabled={!wasmReady || parsing}
                />
            </div>

            {error && <div className="error">{error}</div>}

            {stats && (
                <>
                    <div className="stats-bar">
            <span>
              <strong>File:</strong> {stats.fileName}
            </span>
                        <span>
              <strong>Records:</strong> {stats.totalRecords.toLocaleString()}
            </span>
                        <span>
              <strong>Chunks:</strong> {stats.numChunks}
            </span>
                        <span>
              <strong>Parse:</strong> {stats.parseTimeMs} ms
            </span>
                        {parsing && (
                            <span>
                <strong>Loading:</strong> {Math.round((loadProgress / stats.totalRecords) * 100)}%
              </span>
                        )}
                        {rows.length !== records.length && (
                            <span>
                <strong>Showing:</strong> {rows.length.toLocaleString()} filtered
              </span>
                        )}
                    </div>

                    <div className="search-bar">
                        <input
                            type="text"
                            placeholder="Search all columns…"
                            value={globalFilter}
                            onChange={(e) => setGlobalFilter(e.target.value)}
                            className="search-input"
                        />
                        <div className="export-buttons">
                            <button onClick={() => handleExport("csv")} disabled={parsing}>CSV</button>
                            <button onClick={() => handleExport("json")} disabled={parsing}>JSON</button>
                            <button onClick={() => handleExport("xml")} disabled={parsing}>XML</button>
                        </div>
                    </div>
                </>
            )}

            {detailRecord && (
                <div className="modal-overlay" onClick={() => setDetailRecord(null)}>
                    <div className="modal" onClick={(e) => e.stopPropagation()}>
                        <div className="modal-header">
                            <strong>Record {detailRecord.recordId}</strong>
                            <button className="modal-close" onClick={() => setDetailRecord(null)}>X</button>
                        </div>
                        <div className="modal-tabs">
                            <details open>
                                <summary>Event Data</summary>
                                <pre className="modal-pre">{detailRecord.eventData || "(empty)"}</pre>
                            </details>
                            <details>
                                <summary>XML</summary>
                                <pre className="modal-pre">{formatXml(detailRecord.xml)}</pre>
                            </details>
                        </div>
                    </div>
                </div>
            )}

            {records.length > 0 && (
                <div className="table-container" ref={tableContainerRef}>
                    <table>
                        <thead>
                        {table.getHeaderGroups().map((headerGroup) => (
                            <tr key={headerGroup.id} style={{display: "flex", width: "100%"}}>
                                {headerGroup.headers.map((header) => (
                                    <th
                                        key={header.id}
                                        style={{width: header.getSize(), flex: `0 0 ${header.getSize()}px`}}
                                        onClick={header.column.getToggleSortingHandler()}
                                        className={
                                            header.column.getCanSort() ? "sortable" : undefined
                                        }
                                    >
                                        {header.isPlaceholder
                                            ? null
                                            : flexRender(
                                                header.column.columnDef.header,
                                                header.getContext(),
                                            )}
                                        {sortIndicator(header.column.getIsSorted())}
                                    </th>
                                ))}
                            </tr>
                        ))}
                        </thead>
                        <tbody
                            style={{
                                height: `${virtualizer.getTotalSize()}px`,
                                position: "relative",
                            }}
                        >
                        {virtualizer.getVirtualItems().map((virtualRow) => {
                            const row = rows[virtualRow.index]
                            return (
                                <tr
                                    key={row.id}
                                    style={{
                                        display: "flex",
                                        position: "absolute",
                                        top: 0,
                                        transform: `translateY(${virtualRow.start}px)`,
                                        height: `${ROW_HEIGHT}px`,
                                        width: "100%",
                                    }}
                                >
                                    {row.getVisibleCells().map((cell) => (
                                        <td
                                            key={cell.id}
                                            style={{flex: `0 0 ${cell.column.getSize()}px`}}
                                        >
                                            {flexRender(
                                                cell.column.columnDef.cell,
                                                cell.getContext(),
                                            )}
                                        </td>
                                    ))}
                                </tr>
                            )
                        })}
                        </tbody>
                    </table>
                </div>
            )}
        </div>
    )
}
