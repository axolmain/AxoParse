import {render, screen} from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import {describe, expect, it, vi} from 'vitest'
import {MantineProvider} from '@mantine/core'
import {
    createColumnHelper,
    getCoreRowModel,
    getSortedRowModel,
    useReactTable,
    VisibilityState,
} from '@tanstack/react-table'
import type {ReactNode} from 'react'
import {useState} from 'react'
import type {RecordMeta} from '../../types'
import {EMPTY_FILTERS, FilterState} from '../../lib/filter-types'
import {COLUMN_FILTER_META, ColumnHeader} from '../ColumnHeader'

const columnHelper = createColumnHelper<RecordMeta>()

function makeRecord(overrides: Partial<RecordMeta> = {}): RecordMeta {
    return {
        recordId: 1,
        timestamp: '2024-01-15T10:30:00.000Z',
        chunkIndex: 0,
        recordIndexInChunk: 0,
        eventId: '4624',
        provider: 'Microsoft-Windows-Security-Auditing',
        level: 4,
        levelText: 'Information',
        computer: 'WORKSTATION-1',
        channel: 'Security',
        fileId: 'file-1',
        ...overrides,
    }
}

const TEST_DATA: RecordMeta[] = [makeRecord()]

function Wrapper({children}: { children: ReactNode }) {
    return <MantineProvider>{children}</MantineProvider>
}

interface TestHarnessProps {
    columnId: keyof RecordMeta
    filters?: FilterState
    onFiltersChange?: (f: FilterState) => void
    onColumnVisibilityChange?: (updater: VisibilityState | ((prev: VisibilityState) => VisibilityState)) => void
    availableValues?: string[]
}

function TestHarness({
                         columnId,
                         filters = EMPTY_FILTERS,
                         onFiltersChange = vi.fn(),
                         onColumnVisibilityChange = vi.fn(),
                         availableValues,
                     }: TestHarnessProps) {
    const columns = [columnHelper.accessor(columnId, {header: columnId.charAt(0).toUpperCase() + columnId.slice(1)})]
    const [sorting, setSorting] = useState<{ id: string; desc: boolean }[]>([])

    const table = useReactTable({
        data: TEST_DATA,
        columns,
        state: {sorting},
        onSortingChange: setSorting,
        getCoreRowModel: getCoreRowModel(),
        getSortedRowModel: getSortedRowModel(),
    })

    const header = table.getHeaderGroups()[0].headers[0]

    return (
        <ColumnHeader
            header={header}
            filters={filters}
            onFiltersChange={onFiltersChange}
            onColumnVisibilityChange={onColumnVisibilityChange}
            availableValues={availableValues}
            filterMeta={COLUMN_FILTER_META[columnId]}
        />
    )
}

describe('ColumnHeader', () => {
    it('renders column label', () => {
        render(<TestHarness columnId="provider"/>, {wrapper: Wrapper})
        expect(screen.getByText('Provider')).toBeInTheDocument()
    })

    it('shows sort indicator after clicking label', async () => {
        const user = userEvent.setup()
        render(<TestHarness columnId="provider"/>, {wrapper: Wrapper})

        const label = screen.getByText('Provider')
        await user.click(label)

        expect(screen.getByText(/Provider.*\u2191/)).toBeInTheDocument()
    })

    it('renders inline filter input', () => {
        render(
            <TestHarness
                columnId="provider"
                availableValues={['Microsoft-Windows-Security-Auditing', 'Sysmon']}
            />,
            {wrapper: Wrapper},
        )

        const input = screen.getByPlaceholderText('Filter...')
        expect(input).toBeInTheDocument()
    })

    it('inline filter shows current value when filter is set', () => {
        const filters: FilterState = {...EMPTY_FILTERS, provider: 'Microsoft-Windows-Security-Auditing'}
        render(
            <TestHarness
                columnId="provider"
                filters={filters}
                availableValues={['Microsoft-Windows-Security-Auditing', 'Sysmon']}
            />,
            {wrapper: Wrapper},
        )

        const input = screen.getByRole('textbox', {hidden: true})
        expect(input).toHaveValue('Microsoft-Windows-Security-Auditing')
    })

    it('calls onFiltersChange when filter value selected', async () => {
        const user = userEvent.setup()
        const onFiltersChange = vi.fn()
        render(
            <TestHarness
                columnId="provider"
                onFiltersChange={onFiltersChange}
                availableValues={['Microsoft-Windows-Security-Auditing', 'Sysmon']}
            />,
            {wrapper: Wrapper},
        )

        const input = screen.getByPlaceholderText('Filter...')
        await user.click(input)
        const option = await screen.findByText('Sysmon')
        await user.click(option)

        expect(onFiltersChange).toHaveBeenCalledWith(
            expect.objectContaining({provider: 'Sysmon'}),
        )
    })

    it('menu has sort and hide options', async () => {
        const user = userEvent.setup()
        render(<TestHarness columnId="provider"/>, {wrapper: Wrapper})

        const menuBtn = screen.getByLabelText('provider menu')
        await user.click(menuBtn)

        expect(await screen.findByText('Sort ascending')).toBeInTheDocument()
        expect(screen.getByText('Sort descending')).toBeInTheDocument()
        expect(screen.getByText('Hide column')).toBeInTheDocument()
    })

    it('hide column calls onColumnVisibilityChange', async () => {
        const user = userEvent.setup()
        const onColumnVisibilityChange = vi.fn()
        render(
            <TestHarness columnId="provider" onColumnVisibilityChange={onColumnVisibilityChange}/>,
            {wrapper: Wrapper},
        )

        const menuBtn = screen.getByLabelText('provider menu')
        await user.click(menuBtn)

        const hideItem = await screen.findByText('Hide column')
        await user.click(hideItem)

        expect(onColumnVisibilityChange).toHaveBeenCalled()
        const updater = onColumnVisibilityChange.mock.calls[0][0]
        const result = typeof updater === 'function' ? updater({}) : updater
        expect(result).toEqual({provider: false})
    })
})
