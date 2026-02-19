import {DonutChart} from '@mantine/charts'
import {Paper, Text} from '@mantine/core'

interface LevelDonutProps {
    data: Array<{ name: string; value: number; color: string }>
}

export function LevelDonut({data}: LevelDonutProps) {
    if (data.length === 0) return null

    return (
        <Paper p="sm" withBorder>
            <Text size="sm" fw={600} mb="xs">Level Distribution</Text>
            <DonutChart
                h={200}
                data={data}
                withLabelsLine
                labelsType="percent"
                tooltipDataSource="segment"
            />
        </Paper>
    )
}
