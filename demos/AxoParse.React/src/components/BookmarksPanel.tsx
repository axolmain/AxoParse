import {useCallback} from 'react'
import {ActionIcon, Button, Drawer, Group, Stack, Text, TextInput} from '@mantine/core'
import type {Bookmark} from '../lib/bookmarks'

interface BookmarksPanelProps {
    opened: boolean
    onClose: () => void
    bookmarks: Map<string, Bookmark>
    onSetNote: (key: string, note: string) => void
    onRemove: (key: string) => void
    onClear: () => void
}

export function BookmarksPanel({
                                   opened,
                                   onClose,
                                   bookmarks,
                                   onSetNote,
                                   onRemove,
                                   onClear,
                               }: BookmarksPanelProps) {
    const entries = Array.from(bookmarks.values()).sort((a, b) => b.createdAt - a.createdAt)

    const handleClear = useCallback(() => {
        if (confirm('Remove all bookmarks?')) {
            onClear()
        }
    }, [onClear])

    return (
        <Drawer opened={opened} onClose={onClose} title="Bookmarks" position="right" size="md">
            <Stack gap="sm">
                {entries.length === 0 && (
                    <Text size="sm" c="dimmed">No bookmarks yet. Click the star icon on a row to bookmark it.</Text>
                )}

                {entries.map((b) => (
                    <Group key={b.key} gap="xs" align="flex-start" wrap="nowrap" style={{
                        padding: 'var(--mantine-spacing-xs)',
                        borderRadius: 'var(--mantine-radius-sm)',
                        background: 'var(--mantine-color-gray-0)',
                    }}>
                        <Stack gap={2} style={{flex: 1}}>
                            <Text size="xs" c="dimmed">
                                Chunk {b.chunkIndex} / Record {b.recordIndexInChunk}
                            </Text>
                            <TextInput
                                placeholder="Add a note..."
                                value={b.note}
                                onChange={(e) => onSetNote(b.key, e.currentTarget.value)}
                                size="xs"
                            />
                        </Stack>
                        <ActionIcon
                            variant="subtle"
                            color="red"
                            size="sm"
                            onClick={() => onRemove(b.key)}
                        >
                            x
                        </ActionIcon>
                    </Group>
                ))}

                {entries.length > 0 && (
                    <Button variant="subtle" color="red" size="xs" onClick={handleClear}>
                        Clear all bookmarks
                    </Button>
                )}
            </Stack>
        </Drawer>
    )
}
