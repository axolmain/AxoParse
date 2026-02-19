import {BarChart} from '@mantine/charts'
import {Paper, Text} from '@mantine/core'

interface ProviderChartProps {
    data: Array<{ provider: string; count: number }>
}

export function ProviderChart({data}: ProviderChartProps) {
    if (data.length === 0) return null

    return (
        <Paper p="sm" withBorder>
            <Text size="sm" fw={600} mb="xs">Top Providers</Text>
            <BarChart
                h={Math.max(200, data.length * 28)}
                data={data}
                dataKey="provider"
                series={[{name: 'count', color: 'teal.6'}]}
                orientation="vertical"
                gridAxis="x"
                tickLine="x"
                barProps={{radius: 2}}
            />
        </Paper>
    )
}
