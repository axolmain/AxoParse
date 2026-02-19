import {ActionIcon, Chip, Group, Menu, Popover, Select, Stack, TagsInput, Text, TextInput} from '@mantine/core'
import {DateTimePicker} from '@mantine/dates'
import {Header, VisibilityState} from '@tanstack/react-table'
import type {RecordMeta} from '../types'
import {FilterState} from '../lib/filter-types'
import {LEVEL_CONFIG} from '../lib/level-colors'
import classes from './EventTable.module.css'

const SORT_INDICATORS: Record<string, string> = {asc: ' \u2191', desc: ' \u2193'}

type FilterVariant = 'select' | 'multi-select' | 'tags' | 'chips' | 'time-range'

export interface ColumnFilterMeta {
    filterKey: keyof FilterState
    variant: FilterVariant
}

interface ColumnHeaderProps {
    header: Header<RecordMeta, unknown>
    filters: FilterState
    onFiltersChange: (f: FilterState) => void
    onColumnVisibilityChange: (updater: VisibilityState | ((prev: VisibilityState) => VisibilityState)) => void
    availableValues?: string[]
    filterMeta?: ColumnFilterMeta
}

function InlineFilter({
                          meta,
                          filters,
                          onFiltersChange,
                          availableValues,
                      }: {
    meta: ColumnFilterMeta
    filters: FilterState
    onFiltersChange: (f: FilterState) => void
    availableValues?: string[]
}) {
    const update = <K extends keyof FilterState>(key: K, value: FilterState[K]) => {
        onFiltersChange({...filters, [key]: value})
    }

    switch (meta.variant) {
        case 'select':
        case 'multi-select':
            return (
                <Select
                    placeholder="Filter..."
                    data={availableValues ?? []}
                    value={filters[meta.filterKey] as string | null}
                    onChange={(v) => update(meta.filterKey, v as FilterState[typeof meta.filterKey])}
                    searchable
                    clearable
                    size="xs"
                    styles={{input: {minHeight: 28, height: 28}}}
                />
            )

        case 'tags':
            return (
                <TagsInput
                    placeholder="Filter..."
                    data={availableValues ?? []}
                    value={filters[meta.filterKey] as string[]}
                    onChange={(v) => update(meta.filterKey, v as FilterState[typeof meta.filterKey])}
                    size="xs"
                    clearable
                    styles={{input: {minHeight: 28}}}
                />
            )

        case 'chips':
            return (
                <Popover position="bottom-start" shadow="md" withinPortal>
                    <Popover.Target>
                        <TextInput
                            placeholder="Filter..."
                            readOnly
                            size="xs"
                            value={filters.levels.length > 0
                                ? filters.levels.map((l) => LEVEL_CONFIG[l]?.label ?? `Level ${l}`).join(', ')
                                : ''}
                            styles={{input: {minHeight: 28, height: 28, cursor: 'pointer'}}}
                        />
                    </Popover.Target>
                    <Popover.Dropdown>
                        <Chip.Group
                            multiple
                            value={(filters.levels).map(String)}
                            onChange={(v: string[]) => update('levels', v.map(Number))}
                        >
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
                    </Popover.Dropdown>
                </Popover>
            )

        case 'time-range':
            return (
                <Popover position="bottom-start" shadow="md" withinPortal>
                    <Popover.Target>
                        <TextInput
                            placeholder="Filter..."
                            readOnly
                            size="xs"
                            value={filters.timeRange[0] || filters.timeRange[1]
                                ? [
                                    filters.timeRange[0]?.toLocaleDateString() ?? '',
                                    filters.timeRange[1]?.toLocaleDateString() ?? '',
                                ].filter(Boolean).join(' \u2013 ')
                                : ''}
                            styles={{input: {minHeight: 28, height: 28, cursor: 'pointer'}}}
                        />
                    </Popover.Target>
                    <Popover.Dropdown>
                        <Stack gap="xs">
                            <DateTimePicker
                                placeholder="Start time"
                                value={filters.timeRange[0]}
                                onChange={(v) => update('timeRange', [v ? new Date(v) : null, filters.timeRange[1]])}
                                size="xs"
                                clearable
                                style={{minWidth: 200}}
                            />
                            <DateTimePicker
                                placeholder="End time"
                                value={filters.timeRange[1]}
                                onChange={(v) => update('timeRange', [filters.timeRange[0], v ? new Date(v) : null])}
                                size="xs"
                                clearable
                                style={{minWidth: 200}}
                            />
                        </Stack>
                    </Popover.Dropdown>
                </Popover>
            )
    }
}

export const COLUMN_FILTER_META: Record<string, ColumnFilterMeta> = {
    timestamp: {filterKey: 'timeRange', variant: 'time-range'},
    eventId: {filterKey: 'eventIds', variant: 'tags'},
    level: {filterKey: 'levels', variant: 'chips'},
    provider: {filterKey: 'provider', variant: 'select'},
    computer: {filterKey: 'computer', variant: 'select'},
    channel: {filterKey: 'channel', variant: 'select'},
    fileId: {filterKey: 'fileIds', variant: 'multi-select'},
}

export function ColumnHeader({
                                 header,
                                 filters,
                                 onFiltersChange,
                                 onColumnVisibilityChange,
                                 availableValues,
                                 filterMeta,
                             }: ColumnHeaderProps) {
    const columnId = header.column.id
    const meta = filterMeta ?? COLUMN_FILTER_META[columnId]
    const sortDirection = header.column.getIsSorted()

    return (
        <>
            <div className={classes.headerLabelRow}>
                <Text
                    size="xs"
                    fw={600}
                    c="inherit"
                    style={{flex: 1, cursor: header.column.getCanSort() ? 'pointer' : 'default', userSelect: 'none'}}
                    onClick={header.column.getToggleSortingHandler()}
                >
                    {typeof header.column.columnDef.header === 'string'
                        ? header.column.columnDef.header
                        : columnId}
                    {SORT_INDICATORS[sortDirection as string] ?? ''}
                </Text>

                <Menu shadow="md" position="bottom-end" withinPortal>
                    <Menu.Target>
                        <ActionIcon size="xs" variant="transparent" c="inherit" aria-label={`${columnId} menu`}>
                            {'\u22EE'}
                        </ActionIcon>
                    </Menu.Target>
                    <Menu.Dropdown>
                        <Menu.Item onClick={() => header.column.toggleSorting(false)}>
                            Sort ascending
                        </Menu.Item>
                        <Menu.Item onClick={() => header.column.toggleSorting(true)}>
                            Sort descending
                        </Menu.Item>
                        {sortDirection && (
                            <Menu.Item onClick={() => header.column.clearSorting()}>
                                Clear sort
                            </Menu.Item>
                        )}
                        <Menu.Divider/>
                        <Menu.Item
                            onClick={() => onColumnVisibilityChange((prev) => ({...prev, [columnId]: false}))}
                        >
                            Hide column
                        </Menu.Item>
                    </Menu.Dropdown>
                </Menu>
            </div>

            {meta && (
                <div className={classes.headerFilterRow}>
                    <InlineFilter
                        meta={meta}
                        filters={filters}
                        onFiltersChange={onFiltersChange}
                        availableValues={availableValues}
                    />
                </div>
            )}
        </>
    )
}
