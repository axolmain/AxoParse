import {renderHook} from '@testing-library/react'
import {describe, expect, it} from 'vitest'
import {useFilteredRecords} from '../useFilteredRecords'
import {EMPTY_FILTERS, FilterState} from '../../lib/filter-types'
import type {RecordMeta} from '../../types'

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

const TEST_RECORDS: RecordMeta[] = [
    makeRecord({recordId: 1, computer: 'WORKSTATION-1', provider: 'Microsoft-Windows-Security-Auditing'}),
    makeRecord({recordId: 2, computer: 'WORKSTATION-2', provider: 'Microsoft-Windows-Sysmon'}),
    makeRecord({recordId: 3, computer: 'WORKSTATION-1', provider: 'Microsoft-Windows-Sysmon'}),
    makeRecord({recordId: 4, computer: 'SERVER-DC01', provider: 'Microsoft-Windows-Security-Auditing'}),
]

describe('useFilteredRecords', () => {
    it('returns all records when no filters active', () => {
        const {result} = renderHook(() => useFilteredRecords(TEST_RECORDS, EMPTY_FILTERS))
        expect(result.current).toHaveLength(TEST_RECORDS.length)
    })

    it('filters by computer', () => {
        const filters: FilterState = {...EMPTY_FILTERS, computer: 'WORKSTATION-1'}
        const {result} = renderHook(() => useFilteredRecords(TEST_RECORDS, filters))
        expect(result.current).toHaveLength(2)
        expect(result.current.every((r) => r.computer === 'WORKSTATION-1')).toBe(true)
    })

    it('computer filter combines with provider filter', () => {
        const filters: FilterState = {
            ...EMPTY_FILTERS,
            computer: 'WORKSTATION-1',
            provider: 'Microsoft-Windows-Sysmon',
        }
        const {result} = renderHook(() => useFilteredRecords(TEST_RECORDS, filters))
        expect(result.current).toHaveLength(1)
        expect(result.current[0].recordId).toBe(3)
    })

    it('null computer filter passes all records', () => {
        const filters: FilterState = {...EMPTY_FILTERS, computer: null}
        const {result} = renderHook(() => useFilteredRecords(TEST_RECORDS, filters))
        expect(result.current).toHaveLength(TEST_RECORDS.length)
    })
})
