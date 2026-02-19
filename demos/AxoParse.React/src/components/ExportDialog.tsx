import {useCallback, useRef, useState} from 'react'
import {Button, Checkbox, Group, Modal, Progress, SegmentedControl, Stack, Text} from '@mantine/core'
import type {RecordMeta} from '../types'
import {EXPORT_FIELDS, ExportFormat, exportRecords} from '../export'

interface ExportDialogProps {
    opened: boolean
    onClose: () => void
    filteredRecords: RecordMeta[]
    file: File
    fileName: string
    requestBatchRender: (file: File, records: RecordMeta[]) => Promise<import('../types').EvtxRecord[]>
}

export function ExportDialog({
                                 opened,
                                 onClose,
                                 filteredRecords,
                                 file,
                                 fileName,
                                 requestBatchRender,
                             }: ExportDialogProps) {
    const [format, setFormat] = useState<ExportFormat>('json')
    const [selectedFields, setSelectedFields] = useState<string[]>(EXPORT_FIELDS.map(String))
    const [progress, setProgress] = useState<number | null>(null)
    const [exporting, setExporting] = useState(false)
    const abortRef = useRef<AbortController | null>(null)

    const baseName = fileName.replace(/\.evtx$/i, '')

    const handleExport = useCallback(async () => {
        setExporting(true)
        setProgress(0)
        const abort = new AbortController()
        abortRef.current = abort

        try {
            // Batch render all filtered records
            setProgress(0)
            const rendered = await requestBatchRender(file, filteredRecords)
            if (abort.signal.aborted) return

            setProgress(0.5)

            const fields = format === 'csv'
                ? selectedFields as (keyof import('../types').EvtxRecord)[]
                : undefined

            await exportRecords({
                records: rendered,
                format,
                fileName: baseName,
                fields,
                onProgress: (frac) => setProgress(0.5 + frac * 0.5),
                signal: abort.signal,
            })

            onClose()
        } catch {
            // Aborted or error — silently ignore
        } finally {
            setExporting(false)
            setProgress(null)
            abortRef.current = null
        }
    }, [file, filteredRecords, format, selectedFields, baseName, requestBatchRender, onClose])

    const handleCancel = useCallback(() => {
        if (abortRef.current) {
            abortRef.current.abort()
        }
        setExporting(false)
        setProgress(null)
    }, [])

    return (
        <Modal opened={opened} onClose={exporting ? handleCancel : onClose} title="Export Records" size="md">
            <Stack gap="md">
                <Text size="sm" c="dimmed">
                    {filteredRecords.length.toLocaleString()} records will be exported
                </Text>

                <div>
                    <Text size="sm" fw={500} mb={4}>Format</Text>
                    <SegmentedControl
                        value={format}
                        onChange={(v) => setFormat(v as ExportFormat)}
                        data={[
                            {label: 'JSON', value: 'json'},
                            {label: 'JSONL', value: 'jsonl'},
                            {label: 'CSV', value: 'csv'},
                            {label: 'XML', value: 'xml'},
                        ]}
                        fullWidth
                    />
                </div>

                {format === 'csv' && (
                    <div>
                        <Text size="sm" fw={500} mb={4}>Fields</Text>
                        <Checkbox.Group value={selectedFields} onChange={setSelectedFields}>
                            <Group gap="xs">
                                {EXPORT_FIELDS.map((f) => (
                                    <Checkbox key={f} value={f} label={f} size="xs"/>
                                ))}
                            </Group>
                        </Checkbox.Group>
                    </div>
                )}

                {progress !== null && (
                    <Progress value={progress * 100} animated size="sm"/>
                )}

                <Group justify="flex-end">
                    {exporting ? (
                        <Button variant="subtle" color="red" onClick={handleCancel}>Cancel</Button>
                    ) : (
                        <>
                            <Button variant="subtle" onClick={onClose}>Cancel</Button>
                            <Button onClick={handleExport} disabled={filteredRecords.length === 0}>
                                Export
                            </Button>
                        </>
                    )}
                </Group>
            </Stack>
        </Modal>
    )
}
