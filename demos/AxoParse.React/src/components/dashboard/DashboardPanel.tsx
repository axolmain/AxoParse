import {SimpleGrid} from '@mantine/core'
import type {DashboardStats} from '../../hooks/useDashboardStats'
import {EventIdChart} from './EventIdChart'
import {LevelDonut} from './LevelDonut'
import {ProviderChart} from './ProviderChart'
import {TimelineHeatmap} from './TimelineHeatmap'

interface DashboardPanelProps {
    stats: DashboardStats
    onTimeRangeSelect: (start: Date, end: Date) => void
    zoomDomain?: [number, number] | null
    onZoomChange?: (domain: [number, number] | null) => void
    activeTimeRange?: [Date | null, Date | null]
}

export function DashboardPanel({
                                   stats,
                                   onTimeRangeSelect,
                                   zoomDomain,
                                   onZoomChange,
                                   activeTimeRange
                               }: DashboardPanelProps) {
    return (
        <div style={{flexShrink: 0}}>
            <TimelineHeatmap
                buckets={stats.timeBuckets}
                onBrushSelect={onTimeRangeSelect}
                zoomDomain={zoomDomain}
                onZoomChange={onZoomChange}
                activeTimeRange={activeTimeRange}
            />
            <SimpleGrid cols={{base: 1, sm: 2, lg: 3}} mt="sm">
                <EventIdChart data={stats.eventIdFrequency}/>
                <LevelDonut data={stats.levelDistribution}/>
                <ProviderChart data={stats.providerDistribution}/>
            </SimpleGrid>
        </div>
    )
}
