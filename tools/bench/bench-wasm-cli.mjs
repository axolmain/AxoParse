#!/usr/bin/env node
import fs from 'node:fs'
import path from 'node:path'

const frameworkDir = process.env.CS_WASM_FRAMEWORK
if (!frameworkDir) {
	console.error('CS_WASM_FRAMEWORK env var not set')
	process.exit(1)
}

const file = process.argv[2]
if (!file) {
	console.error('Usage: node bench-wasm-cli.mjs <file.evtx>')
	process.exit(1)
}

try {
	const {dotnet} = await import(path.join(frameworkDir, 'dotnet.js'))
	const runtime = await dotnet.create()
	await runtime.runMain()
	const config = runtime.getConfig()
	const exports = await runtime.getAssemblyExports(config.mainAssemblyName)

	const buf = fs.readFileSync(path.resolve(file))
	// Result intentionally unused — we're benchmarking parse time, not consuming output
	exports.EvtxParserWasm.Browser.EvtxInterop.ParseEvtxToJson(new Uint8Array(buf))
} catch (err) {
	console.error(`WASM benchmark failed: ${err.message}`)
	process.exit(1)
}
