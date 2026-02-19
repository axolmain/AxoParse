import {useMemo} from 'react'
import type {RecordMeta} from '../types'
import {LEVEL_CONFIG} from '../lib/level-colors'
import {bucketByDay, BucketResult, bucketTimestamps} from '../lib/time-buckets'

export interface DashboardStats {
    eventIdFrequency: Array<{ eventId: string; count: number }>
    levelDistribution: Array<{ name: string; value: number; color: string }>
    providerDistribution: Array<{ provider: string; count: number }>
    timeBuckets: BucketResult
    timestamps: number[]
}

const BUCKET_COUNT = 100

export function useDashboardStats(
    records: RecordMeta[],
    zoomDomain?: [number, number] | null,
): DashboardStats {
    return useMemo(() => {
        const eventIdMap = new Map<string, number>()
        const levelMap = new Map<number, number>()
        const providerMap = new Map<string, number>()
        const timestamps: number[] = new Array(records.length)

        for (let i = 0; i < records.length; i++) {
            const r = records[i]
            eventIdMap.set(r.eventId, (eventIdMap.get(r.eventId) ?? 0) + 1)
            levelMap.set(r.level, (levelMap.get(r.level) ?? 0) + 1)
            providerMap.set(r.provider, (providerMap.get(r.provider) ?? 0) + 1)
            timestamps[i] = new Date(r.timestamp).getTime()
        }

        const eventIdFrequency = Array.from(eventIdMap.entries())
            .map(([eventId, count]) => ({eventId, count}))
            .sort((a, b) => b.count - a.count)
            .slice(0, 20)

        const levelDistribution = Array.from(levelMap.entries())
            .map(([level, count]) => {
                const config = LEVEL_CONFIG[level]
                return {
                    name: config?.label ?? `Level ${level}`,
                    value: count,
                    color: config ? `var(--mantine-color-${config.color}-6)` : 'var(--mantine-color-gray-6)',
                }
            })
            .sort((a, b) => b.value - a.value)

        const providerDistribution = Array.from(providerMap.entries())
            .map(([provider, count]) => ({provider, count}))
            .sort((a, b) => b.count - a.count)
            .slice(0, 15)

        const timeBuckets = zoomDomain
            ? bucketTimestamps(timestamps, BUCKET_COUNT, zoomDomain)
            : bucketByDay(timestamps)

        return {eventIdFrequency, levelDistribution, providerDistribution, timeBuckets, timestamps}
    }, [records, zoomDomain])
}
