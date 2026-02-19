import {useMemo} from 'react'
import {BarChart} from '@mantine/charts'
import {Paper, Text} from '@mantine/core'

const MAX_LABEL_LENGTH = 24

interface ProviderChartProps {
    data: Array<{ provider: string; count: number }>
}

export function ProviderChart({data}: ProviderChartProps) {
    if (data.length === 0) return null

    const truncated = useMemo(() =>
        data.map((d) => ({
            ...d,
            provider: d.provider.length > MAX_LABEL_LENGTH
                ? d.provider.slice(0, MAX_LABEL_LENGTH - 1) + '\u2026'
                : d.provider,
        })), [data])

    return (
        <Paper p="sm" withBorder>
            <Text size="sm" fw={600} mb="xs">Top Providers</Text>
            <BarChart
                h={truncated.length * 36 + 30}
                data={truncated}
                dataKey="provider"
                series={[{name: 'count', color: 'teal.6'}]}
                orientation="vertical"
                gridAxis="x"
                tickLine="x"
                barProps={{radius: 2}}
                yAxisProps={{width: 160}}
            />
        </Paper>
    )
}
