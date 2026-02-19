import {useCallback} from 'react'
import {ActionIcon, Badge, Button, CloseButton, Group, Paper, TextInput, Tooltip} from '@mantine/core'
import {activeFilterCount, EMPTY_FILTERS, FilterState} from '../lib/filter-types'
import {LEVEL_CONFIG} from '../lib/level-colors'

interface FilterBarProps {
    filters: FilterState
    onChange: (filters: FilterState) => void
    availableEventIds: string[]
    availableProviders: string[]
    availableChannels: string[]
    availableFiles?: Array<{ value: string; label: string }>
}

function ActiveFilterPills({filters, onChange}: { filters: FilterState; onChange: (f: FilterState) => void }) {
    const pills: Array<{ label: string; onRemove: () => void }> = []

    if (filters.eventIds.length > 0) {
        pills.push({
            label: `Event IDs: ${filters.eventIds.join(', ')}`,
            onRemove: () => onChange({...filters, eventIds: []}),
        })
    }
    if (filters.levels.length > 0) {
        const names = filters.levels.map((l) => LEVEL_CONFIG[l]?.label ?? `Level ${l}`).join(', ')
        pills.push({
            label: `Levels: ${names}`,
            onRemove: () => onChange({...filters, levels: []}),
        })
    }
    if (filters.provider !== null) {
        const short = filters.provider.length > 30 ? filters.provider.slice(0, 27) + '...' : filters.provider
        pills.push({
            label: `Provider: ${short}`,
            onRemove: () => onChange({...filters, provider: null}),
        })
    }
    if (filters.computer !== null) {
        pills.push({
            label: `Computer: ${filters.computer}`,
            onRemove: () => onChange({...filters, computer: null}),
        })
    }
    if (filters.channel !== null) {
        pills.push({
            label: `Channel: ${filters.channel}`,
            onRemove: () => onChange({...filters, channel: null}),
        })
    }
    if (filters.timeRange[0] !== null || filters.timeRange[1] !== null) {
        const start = filters.timeRange[0]?.toLocaleString() ?? '...'
        const end = filters.timeRange[1]?.toLocaleString() ?? '...'
        pills.push({
            label: `Time: ${start} \u2013 ${end}`,
            onRemove: () => onChange({...filters, timeRange: [null, null]}),
        })
    }
    if (filters.fileIds.length > 0) {
        pills.push({
            label: `Files: ${filters.fileIds.length}`,
            onRemove: () => onChange({...filters, fileIds: []}),
        })
    }
    if (filters.bookmarkedOnly) {
        pills.push({
            label: 'Bookmarked only',
            onRemove: () => onChange({...filters, bookmarkedOnly: false}),
        })
    }

    if (pills.length === 0) return null

    return (
        <>
            {pills.map((pill) => (
                <Badge
                    key={pill.label}
                    size="sm"
                    variant="light"
                    rightSection={<CloseButton size="xs" variant="transparent" onClick={pill.onRemove}/>}
                    style={{paddingRight: 4}}
                >
                    {pill.label}
                </Badge>
            ))}
        </>
    )
}

export function FilterBar({
                              filters,
                              onChange,
                          }: FilterBarProps) {
    const count = activeFilterCount(filters)

    const update = useCallback(<K extends keyof FilterState>(key: K, value: FilterState[K]) => {
        onChange({...filters, [key]: value})
    }, [filters, onChange])

    return (
        <Paper p="xs" radius="sm" withBorder style={{flexShrink: 0}}>
            <Group gap="xs" wrap="wrap">
                <Tooltip
                    label={filters.textSearchRegex ? 'Regex mode (click to toggle)' : 'Text mode (click to toggle)'}>
                    <TextInput
                        placeholder="Search records..."
                        value={filters.textSearch}
                        onChange={(e) => update('textSearch', e.currentTarget.value)}
                        size="xs"
                        style={{minWidth: 200, flex: 1, maxWidth: 400}}
                        rightSection={
                            <ActionIcon
                                size="xs"
                                variant={filters.textSearchRegex ? 'filled' : 'subtle'}
                                color={filters.textSearchRegex ? 'blue' : 'gray'}
                                onClick={() => update('textSearchRegex', !filters.textSearchRegex)}
                            >
                                .*
                            </ActionIcon>
                        }
                    />
                </Tooltip>

                <ActiveFilterPills filters={filters} onChange={onChange}/>

                {count > 0 && (
                    <Button size="xs" variant="subtle" color="gray" onClick={() => onChange(EMPTY_FILTERS)}>
                        Clear all ({count})
                    </Button>
                )}
            </Group>
        </Paper>
    )
}
