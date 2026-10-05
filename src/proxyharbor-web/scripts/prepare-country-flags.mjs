import { copyFile, mkdir, readdir } from 'node:fs/promises'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

// Vite serves public assets in development and copies them unchanged during build.
// Only the package's 4x3 SVG flags are prepared; no glob parser or watcher is needed.
export async function prepareCountryFlags(sourceDirectory, outputDirectory) {
  const entries = await readdir(sourceDirectory, { withFileTypes: true })
  const flags = entries.filter(entry => entry.isFile() && /^[a-z]{2}(?:-[a-z]{2,3})?\.svg$/.test(entry.name))
  if (flags.length === 0) throw new Error('Country flag package contains no supported SVG assets.')
  await mkdir(outputDirectory, { recursive: true })
  for (const flag of flags) {
    await copyFile(join(sourceDirectory, flag.name), join(outputDirectory, flag.name))
  }
  return flags.length
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const count = await prepareCountryFlags(
    fileURLToPath(new URL('../node_modules/flag-icons/flags/4x3/', import.meta.url)),
    fileURLToPath(new URL('../public/flags/', import.meta.url)),
  )
  console.log(`Prepared ${count} country flags at /flags/*.svg.`)
}
