import {createFileRoute} from '@tanstack/react-router'
import {useCallback} from 'react'
import {useEvtxWorker} from '../useEvtxWorker'
import {LandingView} from '../components/LandingView'
import {ViewerView} from '../components/ViewerView'

export const Route = createFileRoute('/')({
    component: RouteComponent,
})

function RouteComponent() {
    const worker = useEvtxWorker()

    const hasData = worker.allRecords.length > 0 || worker.anyStreaming

    const handleFileDrop = useCallback((file: File) => {
        worker.addFile(file)
    }, [worker.addFile])

    if (!hasData) {
        return (
            <LandingView
                wasmLoading={worker.wasmLoading}
                wasmReady={worker.wasmReady}
                error={worker.error}
                onFileDrop={handleFileDrop}
            />
        )
    }

    return (
        <ViewerView
            allRecords={worker.allRecords}
            anyStreaming={worker.anyStreaming}
            streamProgress={worker.streamProgress}
            files={worker.files}
            error={worker.error}
            addFile={worker.addFile}
            removeFile={worker.removeFile}
            requestRecordRender={worker.requestRecordRender}
            requestBatchRender={worker.requestBatchRender}
        />
    )
}
