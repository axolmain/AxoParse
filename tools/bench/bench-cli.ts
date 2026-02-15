import fs from 'node:fs'
import path from 'node:path'
import process from 'node:process'
import {parseEvtx} from '../evtx-parser-react/src/parser/evtx-parser'

const file = process.argv[2]
if (!file) {
	process.exit(1)
}

let format = 'json' // default: json (original behavior)
for (let i = 3; i < process.argv.length; i++) {
	if (process.argv[i] === '-o' && i + 1 < process.argv.length) {
		format = process.argv[++i]!
	}
}

const buf = fs.readFileSync(path.resolve(file)).buffer as ArrayBuffer
const result = parseEvtx(buf)

if (format === 'xml') {
	for (const r of result.records) {
		process.stdout.write(r.xml)
	}
} else {
	process.stdout.write(JSON.stringify(result))
}
