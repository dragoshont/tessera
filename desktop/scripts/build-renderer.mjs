import { cp, mkdir } from 'node:fs/promises'
import { execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import path from 'node:path'

const desktop = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const repo = path.resolve(desktop, '..')
execFileSync('npm', ['--prefix', path.join(repo, 'web'), 'run', 'build'], {
  cwd: repo,
  env: { ...process.env, VITE_BASE: '/' },
  stdio: 'inherit',
})
const output = path.join(desktop, 'dist', 'renderer')
await mkdir(output, { recursive: true })
await cp(path.join(repo, 'web', 'dist'), output, { recursive: true })
