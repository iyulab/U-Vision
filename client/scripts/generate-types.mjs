// Generates the wire types (src/lib/contract.gen.ts) from the OpenAPI contract the server publishes
// (server/openapi/u-vision.json). The generated file is committed; CI regenerates it and fails on a diff.
// Client-only types and additions stay in src/lib/types.ts.
import { compile } from 'json-schema-to-typescript'
import { readFileSync, writeFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

const contractPath = fileURLToPath(new URL('../../server/openapi/u-vision.json', import.meta.url))
const outPath = fileURLToPath(new URL('../src/lib/contract.gen.ts', import.meta.url))

const contract = JSON.parse(readFileSync(contractPath, 'utf8'))
// The schemas reference each other as #/components/schemas/X; as one JSON Schema document they are #/$defs/X.
const defs = JSON.parse(JSON.stringify(contract.components.schemas).replaceAll('#/components/schemas/', '#/$defs/'))
const root = {
  title: 'Contract',
  type: 'object',
  additionalProperties: false,
  properties: Object.fromEntries(Object.keys(defs).map((name) => [name, { $ref: `#/$defs/${name}` }])),
  $defs: defs,
}

const banner = '/* Generated from server/openapi/u-vision.json by scripts/generate-types.mjs — do not edit. */'
const body = await compile(root, 'Contract', {
  bannerComment: banner,
  additionalProperties: false,
  unreachableDefinitions: true,
  style: { singleQuote: true, semi: false, printWidth: 120 },
})
writeFileSync(outPath, body.replace(/\r\n/g, '\n'))
console.log(`wrote ${outPath}`)
