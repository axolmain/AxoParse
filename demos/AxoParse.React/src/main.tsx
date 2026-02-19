import {StrictMode} from "react"
import {createRoot} from "react-dom/client"
import "./index.css"
import '@mantine/core/styles.css';
import '@mantine/charts/styles.css';
import '@mantine/dropzone/styles.css';
import '@mantine/dates/styles.css';
import '@mantine/nprogress/styles.css';
import {MantineProvider} from "@mantine/core";
import {createRouter, RouterProvider} from "@tanstack/react-router";
import {routeTree} from './routeTree.gen'

const router = createRouter({routeTree})

// Register the router instance for type safety
declare module '@tanstack/react-router' {
    interface Register {
        router: typeof router
    }
}

createRoot(document.getElementById("root")!).render(
    <StrictMode>
        <MantineProvider>
            <RouterProvider router={router}/>
        </MantineProvider>
    </StrictMode>,
)
