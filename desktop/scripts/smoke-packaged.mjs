import { spawn } from 'node:child_process'
import { mkdtemp, readFile, rm } from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'

const executable = process.env.TESSERA_PACKAGED_APP
  ?? path.resolve('release/mac-arm64/Tessera.app/Contents/MacOS/Tessera')
const directory = await mkdtemp(path.join(os.tmpdir(), 'tessera-packaged-smoke-'))
const marker = path.join(directory, 'ready')
let output = ''

try {
  const child = spawn(executable, [], {
    env: {
      ...process.env,
      TESSERA_ELECTRON_TEST_USER_DATA: path.join(directory, 'profile'),
      TESSERA_ELECTRON_SMOKE_MARKER: marker,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  })
  child.stdout.on('data', (value) => { output = `${output}${value}`.slice(-8192) })
  child.stderr.on('data', (value) => { output = `${output}${value}`.slice(-8192) })
  const result = await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      child.kill('SIGKILL')
      reject(new Error(`Packaged Tessera did not become ready within 30 seconds.\n${output}`))
    }, 30_000)
    child.once('error', (error) => {
      clearTimeout(timeout)
      reject(error)
    })
    child.once('close', (code, signal) => {
      clearTimeout(timeout)
      resolve({ code, signal })
    })
  })
  if (result.code !== 0)
    throw new Error(`Packaged Tessera exited with code ${result.code} (${result.signal ?? 'no signal'}).\n${output}`)
  if (await readFile(marker, 'utf8') !== 'ready')
    throw new Error('Packaged Tessera exited without the renderer-ready marker.')
  console.log(`PACKAGED_SMOKE: PASS (${executable})`)
} finally {
  await rm(directory, { recursive: true, force: true })
}