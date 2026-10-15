import fs from 'node:fs'
import path from 'node:path'
import {fileURLToPath} from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const externalDir = process.env.EXTERNAL_DIR || path.resolve(__dirname, '../../evtx-external')
const wasmPkg = path.resolve(externalDir, 'evtx-rust/evtx-wasm/pkg')

const file = process.argv[2]
if (!file) {
	console.error('Usage: node bench-rust-wasm-cli.mjs <file.evtx>')
	process.exit(1)
}

const {EvtxWasmParser} = await import(path.join(wasmPkg, 'evtx_wasm.js'))
const buf = fs.readFileSync(path.resolve(file))
const parser = new EvtxWasmParser(new Uint8Array(buf))
// Upstream evtx-wasm no longer exposes JSON output; it renders each chunk to an Arrow IPC batch
let rows = 0
for (let i = 0; i < parser.total_chunks; i++) {
	const chunk = parser.chunk_arrow_ipc(i)
	rows += chunk.rows
	chunk.free()
}
parser.free()
process.stdout.write(`${rows}\n`)
