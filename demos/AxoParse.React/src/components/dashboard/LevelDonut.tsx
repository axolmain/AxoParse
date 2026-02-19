import {DonutChart} from '@mantine/charts'
import {ColorSwatch, Group, Paper, Stack, Text} from '@mantine/core'

interface LevelDonutProps {
    data: Array<{ name: string; value: number; color: string }>
}

export function LevelDonut({data}: LevelDonutProps) {
    if (data.length === 0) return null

    return (
        <Paper p="sm" withBorder>
            <Text size="sm" fw={600} mb="xs">Level Distribution</Text>
            <Group align="center" gap="md" wrap="nowrap">
                <DonutChart
                    h={180}
                    data={data}
                    tooltipDataSource="segment"
                    style={{flex: '0 0 auto'}}
                />
                <Stack gap={6}>
                    {data.map((d) => (
                        <Group key={d.name} gap="xs" wrap="nowrap">
                            <ColorSwatch color={d.color} size={12} withShadow={false}/>
                            <Text size="xs" c="dimmed" style={{whiteSpace: 'nowrap'}}>
                                {d.name}
                            </Text>
                            <Text size="xs" fw={600}>
                                {d.value.toLocaleString()}
                            </Text>
                        </Group>
                    ))}
                </Stack>
            </Group>
        </Paper>
    )
}
