import {createFileRoute} from '@tanstack/react-router'
import {useEvtxWorker} from '../useEvtxWorker'
import {LandingView} from '../components/LandingView'
import {ViewerView} from '../components/ViewerView'

export const Route = createFileRoute('/')({
    component: RouteComponent,
})

function RouteComponent() {
    const worker = useEvtxWorker()

    const hasData = worker.allRecords.length > 0 || worker.anyStreaming

    if (!hasData) {
        return (
            <LandingView
                wasmLoading={worker.wasmLoading}
                wasmReady={worker.wasmReady}
                error={worker.error}
                onFileDrop={worker.addFile}
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
            requestRecordRender={worker.requestRecordRender}
            requestBatchRender={worker.requestBatchRender}
        />
    )
}
