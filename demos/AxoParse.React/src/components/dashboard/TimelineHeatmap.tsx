import {useCallback, useEffect, useRef, useState} from 'react'
import {Paper, Text} from '@mantine/core'
import {useResizeObserver} from '@mantine/hooks'
import type {BucketResult} from '../../lib/time-buckets'
import classes from './TimelineHeatmap.module.css'

interface TimelineHeatmapProps {
    buckets: BucketResult
    onBrushSelect: (start: Date, end: Date) => void
    zoomDomain?: [number, number] | null
    onZoomChange?: (domain: [number, number] | null) => void
    activeTimeRange?: [Date | null, Date | null]
}

const CANVAS_HEIGHT = 80
const PADDING_X = 40
const PADDING_Y = 4
const ZOOM_FACTOR = 0.2

function formatTimestamp(ms: number): string {
    const d = new Date(ms)
    return `${d.getMonth() + 1}/${d.getDate()} ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
}

function formatTimestampLong(ms: number): string {
    const d = new Date(ms)
    return d.toLocaleString()
}

export function TimelineHeatmap({
                                    buckets,
                                    onBrushSelect,
                                    zoomDomain,
                                    onZoomChange,
                                    activeTimeRange,
                                }: TimelineHeatmapProps) {
    const canvasRef = useRef<HTMLCanvasElement>(null)
    const [containerRef, rect] = useResizeObserver()
    const [brushStart, setBrushStart] = useState<number | null>(null)
    const [brushEnd, setBrushEnd] = useState<number | null>(null)
    const isDragging = useRef(false)
    const [hoverX, setHoverX] = useState<number | null>(null)
    const [hoverBucket, setHoverBucket] = useState<{
        index: number;
        count: number;
        startMs: number;
        endMs: number
    } | null>(null)

    const width = rect.width || 600

    // Clear brush when active filter is removed
    const prevHadRange = useRef(false)
    useEffect(() => {
        const hasRange = activeTimeRange != null && (activeTimeRange[0] != null || activeTimeRange[1] != null)
        if (prevHadRange.current && !hasRange) {
            setBrushStart(null)
            setBrushEnd(null)
        }
        prevHadRange.current = hasRange
    }, [activeTimeRange])

    const draw = useCallback(() => {
        const canvas = canvasRef.current
        if (!canvas) return
        const ctx = canvas.getContext('2d')
        if (!ctx) return

        const dpr = window.devicePixelRatio || 1
        canvas.width = width * dpr
        canvas.height = CANVAS_HEIGHT * dpr
        ctx.scale(dpr, dpr)
        canvas.style.width = `${width}px`
        canvas.style.height = `${CANVAS_HEIGHT}px`

        ctx.clearRect(0, 0, width, CANVAS_HEIGHT)

        const {buckets: bins, min, max} = buckets
        if (bins.length === 0 || max === min) return

        let maxCount = 0
        for (let i = 0; i < bins.length; i++) {
            if (bins[i] > maxCount) maxCount = bins[i]
        }
        if (maxCount === 0) return

        const drawWidth = width - PADDING_X * 2
        const drawHeight = CANVAS_HEIGHT - PADDING_Y * 2
        const barWidth = drawWidth / bins.length

        for (let i = 0; i < bins.length; i++) {
            const intensity = bins[i] / maxCount
            const r = Math.round(99 + (245 - 99) * (1 - intensity))
            const g = Math.round(102 + (245 - 102) * (1 - intensity))
            const b = Math.round(241 + (245 - 241) * (1 - intensity))
            ctx.fillStyle = `rgb(${r}, ${g}, ${b})`

            const x = PADDING_X + i * barWidth
            const h = Math.max(1, drawHeight * intensity)
            ctx.fillRect(x, PADDING_Y + drawHeight - h, barWidth - 0.5, h)
        }

        // Active filter overlay
        if (activeTimeRange && (activeTimeRange[0] || activeTimeRange[1])) {
            const range = max - min
            const startMs = activeTimeRange[0]?.getTime() ?? min
            const endMs = activeTimeRange[1]?.getTime() ?? max
            const startFrac = Math.max(0, (startMs - min) / range)
            const endFrac = Math.min(1, (endMs - min) / range)
            const overlayLeft = PADDING_X + startFrac * drawWidth
            const overlayRight = PADDING_X + endFrac * drawWidth

            ctx.fillStyle = 'rgba(99, 102, 241, 0.15)'
            ctx.fillRect(overlayLeft, 0, overlayRight - overlayLeft, CANVAS_HEIGHT)
            ctx.strokeStyle = 'rgba(99, 102, 241, 0.5)'
            ctx.lineWidth = 1.5
            ctx.beginPath()
            ctx.moveTo(overlayLeft, 0)
            ctx.lineTo(overlayLeft, CANVAS_HEIGHT)
            ctx.moveTo(overlayRight, 0)
            ctx.lineTo(overlayRight, CANVAS_HEIGHT)
            ctx.stroke()
        }

        // Brush overlay
        if (brushStart !== null && brushEnd !== null) {
            const left = Math.min(brushStart, brushEnd)
            const right = Math.max(brushStart, brushEnd)
            ctx.fillStyle = 'rgba(99, 102, 241, 0.2)'
            ctx.fillRect(left, 0, right - left, CANVAS_HEIGHT)
            ctx.strokeStyle = 'rgba(99, 102, 241, 0.6)'
            ctx.lineWidth = 1
            ctx.strokeRect(left, 0, right - left, CANVAS_HEIGHT)
        }

        // Axis labels
        ctx.fillStyle = '#868e96'
        ctx.font = '10px system-ui, sans-serif'
        ctx.textAlign = 'left'
        ctx.fillText(formatTimestamp(min), PADDING_X, CANVAS_HEIGHT - 1)
        ctx.textAlign = 'right'
        ctx.fillText(formatTimestamp(max), width - PADDING_X, CANVAS_HEIGHT - 1)
    }, [width, buckets, brushStart, brushEnd, activeTimeRange])

    useEffect(() => {
        draw()
    }, [draw])

    const xToTime = useCallback((x: number): number => {
        const drawWidth = width - PADDING_X * 2
        const frac = Math.max(0, Math.min(1, (x - PADDING_X) / drawWidth))
        return buckets.min + frac * (buckets.max - buckets.min)
    }, [width, buckets])

    const xToBucketIndex = useCallback((x: number): number => {
        const drawWidth = width - PADDING_X * 2
        const frac = Math.max(0, Math.min(1, (x - PADDING_X) / drawWidth))
        return Math.min(Math.floor(frac * buckets.buckets.length), buckets.buckets.length - 1)
    }, [width, buckets])

    const handleMouseDown = useCallback((e: React.MouseEvent) => {
        const rect = canvasRef.current?.getBoundingClientRect()
        if (!rect) return
        const x = e.clientX - rect.left
        setBrushStart(x)
        setBrushEnd(x)
        isDragging.current = true
    }, [])

    const handleMouseMove = useCallback((e: React.MouseEvent) => {
        const canvasRect = canvasRef.current?.getBoundingClientRect()
        if (!canvasRect) return
        const x = e.clientX - canvasRect.left

        if (isDragging.current) {
            setBrushEnd(x)
            setHoverBucket(null)
        } else {
            setHoverX(x)
            const idx = xToBucketIndex(x)
            if (idx >= 0 && idx < buckets.buckets.length) {
                const startMs = buckets.min + idx * buckets.bucketSize
                const endMs = startMs + buckets.bucketSize
                setHoverBucket({index: idx, count: buckets.buckets[idx], startMs, endMs})
            } else {
                setHoverBucket(null)
            }
        }
    }, [xToBucketIndex, buckets])

    const handleMouseLeave = useCallback(() => {
        setHoverX(null)
        setHoverBucket(null)
        if (isDragging.current) handleMouseUp()
    }, [])

    const handleMouseUp = useCallback(() => {
        if (!isDragging.current || brushStart === null || brushEnd === null) return
        isDragging.current = false

        const left = Math.min(brushStart, brushEnd)
        const right = Math.max(brushStart, brushEnd)

        if (right - left < 5) {
            setBrushStart(null)
            setBrushEnd(null)
            return
        }

        const startTime = xToTime(left)
        const endTime = xToTime(right)
        onBrushSelect(new Date(startTime), new Date(endTime))
    }, [brushStart, brushEnd, xToTime, onBrushSelect])

    const handleWheel = useCallback((e: React.WheelEvent) => {
        e.preventDefault()
        if (!onZoomChange) return

        const canvasRect = canvasRef.current?.getBoundingClientRect()
        if (!canvasRect) return

        const x = e.clientX - canvasRect.left
        const drawWidth = width - PADDING_X * 2
        const frac = Math.max(0, Math.min(1, (x - PADDING_X) / drawWidth))

        const currentMin = buckets.min
        const currentMax = buckets.max
        const range = currentMax - currentMin
        if (range <= 0) return

        const cursorTime = currentMin + frac * range
        const direction = e.deltaY > 0 ? 1 : -1
        const scale = 1 + direction * ZOOM_FACTOR

        let newMin = cursorTime - (cursorTime - currentMin) * scale
        let newMax = cursorTime + (currentMax - cursorTime) * scale

        // Clamp: don't zoom out past full range (zoomDomain === null means full)
        if (newMin <= currentMin && newMax >= currentMax && zoomDomain === null) return

        // If zoomed out past original, reset
        if (scale > 1 && (newMax - newMin) >= (currentMax - currentMin) * 1.5) {
            onZoomChange(null)
            return
        }

        // Minimum zoom window: 1 second
        if (newMax - newMin < 1000) return

        onZoomChange([newMin, newMax])
    }, [width, buckets, zoomDomain, onZoomChange])

    const handleDoubleClick = useCallback(() => {
        if (onZoomChange) onZoomChange(null)
    }, [onZoomChange])

    if (buckets.buckets.length === 0) return null

    return (
        <Paper p="sm" withBorder ref={containerRef}>
            <Text size="sm" fw={600} mb="xs">
                Event Timeline
                {zoomDomain && (
                    <Text span size="xs" c="dimmed" ml="xs">
                        (zoomed — double-click to reset)
                    </Text>
                )}
            </Text>
            <div className={classes.wrapper}>
                <canvas
                    ref={canvasRef}
                    className={classes.canvas}
                    onMouseDown={handleMouseDown}
                    onMouseMove={handleMouseMove}
                    onMouseUp={handleMouseUp}
                    onMouseLeave={handleMouseLeave}
                    onWheel={handleWheel}
                    onDoubleClick={handleDoubleClick}
                />
                {hoverBucket && hoverX !== null && !isDragging.current && (
                    <div
                        className={classes.tooltip}
                        style={{left: hoverX, top: -32}}
                    >
                        {formatTimestampLong(hoverBucket.startMs)} — {hoverBucket.count} events
                    </div>
                )}
            </div>
        </Paper>
    )
}
