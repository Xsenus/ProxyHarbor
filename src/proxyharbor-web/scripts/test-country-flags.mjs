import assert from 'node:assert/strict'
import { mkdtemp, mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { test } from 'node:test'
import { prepareCountryFlags } from './prepare-country-flags.mjs'

async function withDirectories(run) {
  const prefix = join(tmpdir(), 'proxyharbor-country-flags-')
  const directory = await mkdtemp(prefix)
  try {
    const source = join(directory, 'source')
    const output = join(directory, 'public', 'flags')
    await mkdir(source)
    await run(source, output)
  } finally {
    assert.ok(resolve(directory).startsWith(resolve(prefix)))
    await rm(directory, { recursive: true, force: true })
  }
}

test('copies country and subdivision SVGs unchanged and refreshes existing assets', async () => {
  await withDirectories(async (source, output) => {
    await writeFile(join(source, 'us.svg'), '<svg>US</svg>')
    await writeFile(join(source, 'gb-eng.svg'), '<svg>England</svg>')
    await writeFile(join(source, 'README.md'), 'not a flag')
    await mkdir(join(source, 'ru.svg'))
    assert.equal(await prepareCountryFlags(source, output), 2)
    assert.deepEqual((await readdir(output)).sort(), ['gb-eng.svg', 'us.svg'])
    assert.equal(await readFile(join(output, 'gb-eng.svg'), 'utf8'), '<svg>England</svg>')
    await writeFile(join(source, 'us.svg'), '<svg>updated</svg>')
    assert.equal(await prepareCountryFlags(source, output), 2)
    assert.equal(await readFile(join(output, 'us.svg'), 'utf8'), '<svg>updated</svg>')
  })
})

test('fails the build when the package is missing', async () => {
  await withDirectories(async (source, output) => {
    await assert.rejects(prepareCountryFlags(join(source, 'missing'), output), { code: 'ENOENT' })
  })
})

test('fails the build when no supported flags exist', async () => {
  await withDirectories(async (source, output) => {
    await writeFile(join(source, 'README.md'), 'empty package')
    await assert.rejects(prepareCountryFlags(source, output), /no supported SVG assets/)
  })
})
