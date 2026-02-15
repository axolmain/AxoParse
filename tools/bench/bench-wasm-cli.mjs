import fs from 'node:fs'
import path from 'node:path'
import {fileURLToPath} from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const frameworkDir = path.resolve(__dirname, '../evtx-parser-react/public/_framework')

const file = process.argv[2]
if (!file) {
	console.error('Usage: node bench-wasm-cli.mjs <file.evtx>')
	process.exit(1)
}

// Dynamically import the .NET WASM runtime
const {dotnet} = await import(path.join(frameworkDir, 'dotnet.js'))
const runtime = await dotnet.create()
await runtime.runMain()
const config = runtime.getConfig()
const exports = await runtime.getAssemblyExports(config.mainAssemblyName)

const buf = fs.readFileSync(path.resolve(file))
const _json = exports.EvtxParserWasm.Browser.EvtxInterop.ParseEvtxToJson(new Uint8Array(buf))
