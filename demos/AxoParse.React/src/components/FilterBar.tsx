import {useCallback} from 'react'
import {ActionIcon, Badge, Button, Chip, Group, MultiSelect, Select, TagsInput, TextInput, Tooltip} from '@mantine/core'
import {DateTimePicker} from '@mantine/dates'
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

export function FilterBar({
                              filters,
                              onChange,
                              availableEventIds,
                              availableProviders,
                              availableChannels,
                              availableFiles,
                          }: FilterBarProps) {
    const count = activeFilterCount(filters)

    const update = useCallback(<K extends keyof FilterState>(key: K, value: FilterState[K]) => {
        onChange({...filters, [key]: value})
    }, [filters, onChange])

    return (
        <Group gap="xs" py="xs" wrap="wrap" style={{flexShrink: 0}}>
            <TagsInput
                placeholder="Event IDs"
                data={availableEventIds}
                value={filters.eventIds}
                onChange={(v) => update('eventIds', v)}
                size="xs"
                style={{minWidth: 160, maxWidth: 260}}
                clearable
            />

            <Chip.Group multiple value={filters.levels.map(String)} onChange={(v) => update('levels', v.map(Number))}>
                <Group gap={4}>
                    {Object.entries(LEVEL_CONFIG)
                        .filter(([k]) => Number(k) >= 1 && Number(k) <= 5)
                        .map(([k, cfg]) => (
                            <Chip key={k} value={k} size="xs" color={cfg.color} variant="light">
                                {cfg.label}
                            </Chip>
                        ))}
                </Group>
            </Chip.Group>

            <Select
                placeholder="Provider"
                data={availableProviders}
                value={filters.provider}
                onChange={(v) => update('provider', v)}
                searchable
                clearable
                size="xs"
                style={{minWidth: 180, maxWidth: 280}}
            />

            <Select
                placeholder="Channel"
                data={availableChannels}
                value={filters.channel}
                onChange={(v) => update('channel', v)}
                searchable
                clearable
                size="xs"
                style={{minWidth: 140, maxWidth: 200}}
            />

            <DateTimePicker
                placeholder="Start time"
                value={filters.timeRange[0]}
                onChange={(v) => update('timeRange', [v ? new Date(v) : null, filters.timeRange[1]])}
                size="xs"
                clearable
                style={{minWidth: 180}}
            />

            <DateTimePicker
                placeholder="End time"
                value={filters.timeRange[1]}
                onChange={(v) => update('timeRange', [filters.timeRange[0], v ? new Date(v) : null])}
                size="xs"
                clearable
                style={{minWidth: 180}}
            />

            {availableFiles && availableFiles.length > 1 && (
                <MultiSelect
                    placeholder="Source files"
                    data={availableFiles}
                    value={filters.fileIds}
                    onChange={(v) => update('fileIds', v)}
                    size="xs"
                    clearable
                    style={{minWidth: 180, maxWidth: 300}}
                />
            )}

            <Tooltip label={filters.textSearchRegex ? 'Regex mode (click to toggle)' : 'Text mode (click to toggle)'}>
                <TextInput
                    placeholder="Search..."
                    value={filters.textSearch}
                    onChange={(e) => update('textSearch', e.currentTarget.value)}
                    size="xs"
                    style={{minWidth: 160}}
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

            {count > 0 && (
                <>
                    <Badge size="sm" variant="filled" color="grape">{count} active</Badge>
                    <Button size="xs" variant="subtle" color="gray" onClick={() => onChange(EMPTY_FILTERS)}>
                        Clear all
                    </Button>
                </>
            )}
        </Group>
    )
}
