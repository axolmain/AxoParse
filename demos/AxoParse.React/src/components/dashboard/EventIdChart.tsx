import {BarChart} from '@mantine/charts'
import {Paper, Text} from '@mantine/core'

interface EventIdChartProps {
    data: Array<{ eventId: string; count: number }>
}

export function EventIdChart({data}: EventIdChartProps) {
    if (data.length === 0) return null

    return (
        <Paper p="sm" withBorder>
            <Text size="sm" fw={600} mb="xs">Top Event IDs</Text>
            <BarChart
                h={data.length * 36 + 30}
                data={data}
                dataKey="eventId"
                series={[{name: 'count', color: 'indigo.6'}]}
                orientation="vertical"
                gridAxis="x"
                tickLine="x"
                barProps={{radius: 2}}
            />
        </Paper>
    )
}
