import {Code, Loader, Stack, Table, Tabs, Text} from '@mantine/core'
import type {EvtxRecord} from '../types'
import {formatXml} from '../format-xml'

interface RowDetailProps {
    record: EvtxRecord | 'loading'
}

function parseEventData(raw: string): Array<[string, string]> {
    if (!raw) return []
    const pairs: Array<[string, string]> = []
    const parts = raw.split(/;\s*/)
    for (const part of parts) {
        const eqIdx = part.indexOf('=')
        if (eqIdx > 0) {
            pairs.push([part.slice(0, eqIdx).trim(), part.slice(eqIdx + 1).trim()])
        } else if (part.trim()) {
            pairs.push(['', part.trim()])
        }
    }
    return pairs
}

export function RowDetail({record}: RowDetailProps) {
    if (record === 'loading') {
        return (
            <Stack align="center" py="md">
                <Loader size="sm"/>
                <Text size="xs" c="dimmed">Loading record details...</Text>
            </Stack>
        )
    }

    const eventDataPairs = parseEventData(record.eventData)

    return (
        <Tabs defaultValue="eventdata" keepMounted={false}>
            <Tabs.List>
                <Tabs.Tab value="eventdata">Event Data</Tabs.Tab>
                <Tabs.Tab value="xml">XML</Tabs.Tab>
                <Tabs.Tab value="metadata">Metadata</Tabs.Tab>
            </Tabs.List>

            <Tabs.Panel value="eventdata" pt="xs">
                {eventDataPairs.length > 0 ? (
                    <Table striped highlightOnHover withTableBorder>
                        <Table.Thead>
                            <Table.Tr>
                                <Table.Th>Key</Table.Th>
                                <Table.Th>Value</Table.Th>
                            </Table.Tr>
                        </Table.Thead>
                        <Table.Tbody>
                            {eventDataPairs.map(([key, value], i) => (
                                <Table.Tr key={i}>
                                    <Table.Td fw={500} style={{whiteSpace: 'nowrap'}}>{key}</Table.Td>
                                    <Table.Td style={{wordBreak: 'break-all'}}>{value}</Table.Td>
                                </Table.Tr>
                            ))}
                        </Table.Tbody>
                    </Table>
                ) : (
                    <Text size="sm" c="dimmed" py="sm">(no event data)</Text>
                )}
            </Tabs.Panel>

            <Tabs.Panel value="xml" pt="xs">
                <Code block style={{maxHeight: 400, overflow: 'auto', fontSize: '0.75rem'}}>
                    {formatXml(record.xml)}
                </Code>
            </Tabs.Panel>

            <Tabs.Panel value="metadata" pt="xs">
                <Table withTableBorder>
                    <Table.Tbody>
                        {[
                            ['Record ID', record.recordId],
                            ['Process ID', record.processId],
                            ['Thread ID', record.threadId],
                            ['Security User ID', record.securityUserId],
                            ['Activity ID', record.activityId],
                            ['Related Activity ID', record.relatedActivityId],
                            ['Task', record.task],
                            ['Opcode', record.opcode],
                            ['Keywords', record.keywords],
                            ['Version', record.version],
                        ].filter(([, v]) => v !== '' && v !== undefined && v !== null).map(([label, value]) => (
                            <Table.Tr key={label as string}>
                                <Table.Td fw={500} style={{whiteSpace: 'nowrap'}}>{label}</Table.Td>
                                <Table.Td style={{wordBreak: 'break-all'}}>{String(value)}</Table.Td>
                            </Table.Tr>
                        ))}
                    </Table.Tbody>
                </Table>
            </Tabs.Panel>
        </Tabs>
    )
}
