import {useEffect, useRef} from 'react'
import {nprogress} from '@mantine/nprogress'

export function useStreamProgress(streaming: boolean, chunksProcessed: number, totalChunks: number): void {
    const wasStreaming = useRef(false)

    useEffect(() => {
        if (streaming && !wasStreaming.current) {
            nprogress.reset()
            nprogress.start()
            wasStreaming.current = true
        }

        if (streaming && totalChunks > 0) {
            const pct = (chunksProcessed / totalChunks) * 100
            nprogress.set(pct)
        }

        if (!streaming && wasStreaming.current) {
            nprogress.complete()
            wasStreaming.current = false
        }
    }, [streaming, chunksProcessed, totalChunks])
}
