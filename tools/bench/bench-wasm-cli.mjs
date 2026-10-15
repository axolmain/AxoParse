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
	// Paged API (what the web app uses): ParseEvtxToJson builds one string per file, which Node's UTF-16
	// decoder rejects past 256 MB. Pages are rendered and marshalled but intentionally unused.
	const interop = exports.AxoParse.Browser.EvtxInterop
	const {totalRecords} = JSON.parse(interop.ParseEvtxFile(new Uint8Array(buf)))
	const pageSize = 1000
	for (let offset = 0; offset < totalRecords; offset += pageSize) {
		interop.GetRecordPage(offset, pageSize)
	}
} catch (err) {
	console.error(`WASM benchmark failed: ${err.message}`)
	process.exit(1)
}
