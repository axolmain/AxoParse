import type {ReactNode} from 'react'
import {ActionIcon, Badge, Group, Paper, Text, Tooltip} from '@mantine/core'
import type {FileSession} from '../types'

interface StatsBarProps {
    fileName: string | null
    totalRecords: number
    numChunks: number
    parseTimeMs: number
    recordCount: number
    filteredCount: number
    streaming: boolean
    chunksProcessed: number
    totalChunks: number
    showDashboard: boolean
    onToggleDashboard: () => void
    onExport?: () => void
    files?: FileSession[]
    bookmarkCount?: number
    onOpenBookmarks?: () => void
    trailing?: ReactNode
}

export function StatsBar({
                             fileName,
                             totalRecords,
                             numChunks,
                             parseTimeMs,
                             recordCount,
                             filteredCount,
                             streaming,
                             chunksProcessed,
                             totalChunks,
                             showDashboard,
                             onToggleDashboard,
                             onExport,
                             files,
                             bookmarkCount,
                             onOpenBookmarks,
                             trailing,
                         }: StatsBarProps) {
    const multiFile = files && files.length > 1

    return (
        <Paper p="xs" radius="sm" withBorder style={{flexShrink: 0}}>
            <Group gap="lg">
            {fileName && <Text size="sm"><Text span fw={600}>File:</Text> {fileName}</Text>}
            <Text size="sm">
                <Text span fw={600}>Records:</Text> {totalRecords.toLocaleString()}
            </Text>
            <Text size="sm">
                <Text span fw={600}>Chunks:</Text> {numChunks}
            </Text>
            <Text size="sm">
                <Text span fw={600}>Parse:</Text> {parseTimeMs} ms
            </Text>
            {streaming && !multiFile && (
                <Badge variant="light" color="blue" size="sm">
                    Parsing {chunksProcessed}/{totalChunks}
                </Badge>
            )}
            {streaming && multiFile && files.filter((f) => f.streaming).map((f) => (
                <Badge key={f.fileId} variant="light" color="blue" size="sm">
                    {f.fileName.slice(0, 12)} {f.streamProgress.chunksProcessed}/{f.streamProgress.totalChunks}
                </Badge>
            ))}
            {filteredCount !== recordCount && (
                <Badge variant="light" color="grape" size="sm">
                    {filteredCount.toLocaleString()} filtered
                </Badge>
            )}
            <div style={{flex: 1}}/>
            {onOpenBookmarks && (
                <Tooltip label="Bookmarks">
                    <ActionIcon variant="light" color="yellow" size="sm" onClick={onOpenBookmarks}>
                        {bookmarkCount && bookmarkCount > 0 ? `\u2605 ${bookmarkCount}` : '\u2606'}
                    </ActionIcon>
                </Tooltip>
            )}
            {onExport && (
                <Tooltip label="Export records">
                    <ActionIcon variant="light" color="teal" size="sm" onClick={onExport}>
                        ↓
                    </ActionIcon>
                </Tooltip>
            )}
            {trailing}
            <Tooltip label={showDashboard ? 'Hide dashboard' : 'Show dashboard'}>
                <ActionIcon
                    variant={showDashboard ? 'filled' : 'light'}
                    color="indigo"
                    size="sm"
                    onClick={onToggleDashboard}
                >
                    {showDashboard ? '▼' : '▶'}
                </ActionIcon>
            </Tooltip>
        </Group>
        </Paper>
    )
}
