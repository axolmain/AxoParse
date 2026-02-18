import {useCallback, useEffect, useMemo, useRef, useState} from "react"
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
import {initAxoParse, parseEvtxFile, streamRecords} from "./axoparse"
import type {EvtxRecord} from "./types"

const WASM_FRAMEWORK_URL = "/wasm/_framework"
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
            <div className="cell-eventdata" title={info.getValue()}>
                {info.getValue()}
            </div>
        ),
    }),
]

interface Stats {
    totalRecords: number
    numChunks: number
    parseTimeMs: number
    loadedRecords: number
    fileName: string
}

export default function App() {
    const [records, setRecords] = useState<EvtxRecord[]>([])
    const [parsing, setParsing] = useState(false)
    const [error, setError] = useState<string | null>(null)
    const [stats, setStats] = useState<Stats | null>(null)
    const [wasmReady, setWasmReady] = useState(false)
    const [wasmLoading, setWasmLoading] = useState(true)
    const [globalFilter, setGlobalFilter] = useState("")
    const [sorting, setSorting] = useState<SortingState>([])
    const initCalled = useRef(false)
    const tableContainerRef = useRef<HTMLDivElement>(null)

    useEffect(() => {
        if (initCalled.current) return
        initCalled.current = true

        initAxoParse(WASM_FRAMEWORK_URL)
            .then(() => {
                setWasmReady(true)
                setWasmLoading(false)
            })
            .catch((err: unknown) => {
                const message = err instanceof Error ? err.message : String(err)
                setError(`Failed to load WASM module: ${message}`)
                setWasmLoading(false)
            })
    }, [])

    const handleFile = useCallback(
        (file: File) => {
            if (!wasmReady) {
                setError("WASM module is still loading — please wait")
                return
            }

            setError(null)
            setParsing(true)
            setRecords([])
            setStats(null)
            setGlobalFilter("")
            setSorting([])

            const reader = new FileReader()
            reader.onload = () => {
                try {
                    const data = new Uint8Array(reader.result as ArrayBuffer)

                    const t0 = performance.now()
                    const meta = parseEvtxFile(data)
                    const parseTimeMs = Math.round(performance.now() - t0)

                    setStats({
                        totalRecords: meta.totalRecords,
                        numChunks: meta.numChunks,
                        parseTimeMs,
                        loadedRecords: 0,
                        fileName: file.name,
                    })

                    streamRecords(meta.totalRecords, (loaded) => {
                        setRecords([...loaded])
                        setStats((prev) =>
                            prev ? {...prev, loadedRecords: loaded.length} : prev,
                        )
                    }).then(() => {
                        setParsing(false)
                    })
                } catch (err: unknown) {
                    const message = err instanceof Error ? err.message : String(err)
                    setError(`Parse failed: ${message}`)
                    setParsing(false)
                }
            }
            reader.onerror = () => {
                setError("Failed to read file")
                setParsing(false)
            }
            reader.readAsArrayBuffer(file)
        },
        [wasmReady],
    )

    const onFileChange = useCallback(
        (e: React.ChangeEvent<HTMLInputElement>) => {
            const file = e.target.files?.[0]
            if (file) handleFile(file)
        },
        [handleFile],
    )

    const table = useReactTable({
        data: records,
        columns,
        state: {sorting, globalFilter},
        onSortingChange: setSorting,
        onGlobalFilterChange: setGlobalFilter,
        getCoreRowModel: getCoreRowModel(),
        getSortedRowModel: getSortedRowModel(),
        getFilteredRowModel: getFilteredRowModel(),
    })

    const {rows} = table.getRowModel()

    const virtualizer = useVirtualizer({
        count: rows.length,
        getScrollElement: () => tableContainerRef.current,
        estimateSize: () => ROW_HEIGHT,
        overscan: 20,
    })

    const sortIndicator = useCallback((sorted: false | "asc" | "desc") => {
        if (sorted === "asc") return " ↑"
        if (sorted === "desc") return " ↓"
        return ""
    }, [])

    const loadProgress = useMemo(() => {
        if (!stats || stats.totalRecords === 0) return 100
        return Math.round((stats.loadedRecords / stats.totalRecords) * 100)
    }, [stats])

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
                        {loadProgress < 100 && (
                            <span>
                <strong>Loading:</strong> {loadProgress}%
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
                    </div>
                </>
            )}

            {records.length > 0 && (
                <div className="table-container" ref={tableContainerRef}>
                    <table>
                        <thead>
                        {table.getHeaderGroups().map((headerGroup) => (
                            <tr key={headerGroup.id}>
                                {headerGroup.headers.map((header) => (
                                    <th
                                        key={header.id}
                                        style={{width: header.getSize()}}
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
                                        position: "absolute",
                                        top: 0,
                                        transform: `translateY(${virtualRow.start}px)`,
                                        height: `${ROW_HEIGHT}px`,
                                        width: "100%",
                                    }}
                                >
                                    {row.getVisibleCells().map((cell) => (
                                        <td key={cell.id}>
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
