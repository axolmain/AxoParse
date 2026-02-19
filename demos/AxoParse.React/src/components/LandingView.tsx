import type {ReactNode} from 'react'
import {Center, Loader, Stack, Text, Title} from '@mantine/core'
import {Dropzone} from '@mantine/dropzone'

interface LandingViewProps {
    wasmLoading: boolean
    wasmReady: boolean
    error: string | null
    onFileDrop: (file: File) => void
}

function WasmStatus({wasmLoading, wasmReady, error, onFileDrop}: LandingViewProps): ReactNode {
    if (wasmLoading) {
        return (
            <Stack align="center" gap="sm">
                <Loader size="md"/>
                <Text size="sm" c="dimmed">Loading WASM module...</Text>
            </Stack>
        )
    }

    if (!wasmReady) {
        return (
            <Text c="red" ta="center">
                {error || 'WASM module failed to load'}
            </Text>
        )
    }

    return (
        <Dropzone
            onDrop={(files) => {
                if (files.length > 0) onFileDrop(files[0])
            }}
            accept={{'application/octet-stream': ['.evtx']}}
            maxFiles={1}
            style={{width: '100%'}}
            p="xl"
        >
            <Stack align="center" gap="sm" py="lg">
                <Text size="xl" fw={500}>
                    Drop an .evtx file here
                </Text>
                <Text size="sm" c="dimmed">
                    or click to browse
                </Text>
            </Stack>
        </Dropzone>
    )
}

export function LandingView(props: LandingViewProps) {
    const {error, wasmReady} = props

    return (
        <Center style={{flex: 1}}>
            <Stack align="center" gap="xl" maw={600}>
                <Stack align="center" gap="xs">
                    <Title order={1}>AxoParse</Title>
                    <Text size="lg" c="dimmed" ta="center">
                        Parse Windows Event Logs in your browser. Nothing leaves your machine.
                    </Text>
                </Stack>

                <WasmStatus {...props}/>

                {error && wasmReady && (
                    <Text c="red" size="sm" ta="center">{error}</Text>
                )}
            </Stack>
        </Center>
    )
}
