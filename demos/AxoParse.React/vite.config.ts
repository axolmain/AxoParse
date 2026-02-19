import {defineConfig} from "vite"
import react from "@vitejs/plugin-react"
import tanstackRouter from "@tanstack/router-plugin/vite";

export default defineConfig({
    plugins: [
        tanstackRouter({
            target: 'react',
            autoCodeSplitting: true,
        }),
        react()
    ],
    server: {
        headers: {
            "Cross-Origin-Embedder-Policy": "require-corp",
            "Cross-Origin-Opener-Policy": "same-origin",
        },
    },
})
