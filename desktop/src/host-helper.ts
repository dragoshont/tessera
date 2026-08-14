import { access } from 'node:fs/promises'
import { execFile } from 'node:child_process'
import { promisify } from 'node:util'

const executeFile = promisify(execFile)
const states = new Set(['ENABLED', 'DISABLED', 'REQUIRES_APPROVAL', 'NOT_FOUND', 'UNAVAILABLE'])

export interface MacHostStatus {
  available: boolean
  state: 'CLIENT_ONLY' | 'ENABLED' | 'DISABLED' | 'REQUIRES_APPROVAL' | 'NOT_FOUND' | 'UNAVAILABLE'
  bundleIdentifier: 'ro.hont.tessera.host'
}

export type HostControlExecutor = (
  file: string,
  args: readonly string[],
) => Promise<{ stdout: string; stderr: string }>

async function defaultExecutor(file: string, args: readonly string[]) {
  return executeFile(file, [...args], {
    encoding: 'utf8',
    timeout: 15_000,
    maxBuffer: 64 * 1024,
    windowsHide: true,
    env: { PATH: '/usr/bin:/bin' },
  })
}

export class MacHostHelperManager {
  constructor(
    private readonly controlPath: string,
    private readonly execute: HostControlExecutor = defaultExecutor,
  ) {}

  async status(): Promise<MacHostStatus> {
    try {
      await access(this.controlPath)
    } catch {
      return { available: false, state: 'CLIENT_ONLY', bundleIdentifier: 'ro.hont.tessera.host' }
    }
    return this.run('status')
  }

  async setEnabled(enabled: boolean): Promise<MacHostStatus> {
    if (typeof enabled !== 'boolean') throw new Error('Mac Host enabled state is invalid.')
    try {
      await access(this.controlPath)
    } catch {
      if (!enabled) return { available: false, state: 'CLIENT_ONLY', bundleIdentifier: 'ro.hont.tessera.host' }
      throw new Error('Mac Host helper is not bundled.')
    }
    return this.run(enabled ? 'register' : 'unregister')
  }

  private async run(verb: 'status' | 'register' | 'unregister'): Promise<MacHostStatus> {
    const { stdout, stderr } = await this.execute(this.controlPath, [verb])
    if (stderr.trim() !== '' || Buffer.byteLength(stdout, 'utf8') > 64 * 1024)
      throw new Error('Mac Host control returned an invalid response.')
    let value: unknown
    try { value = JSON.parse(stdout) } catch { throw new Error('Mac Host control returned invalid JSON.') }
    if (!value || typeof value !== 'object' || Array.isArray(value))
      throw new Error('Mac Host status is invalid.')
    const candidate = value as Record<string, unknown>
    if (
      Object.keys(candidate).sort().join(',') !== 'available,bundleIdentifier,state' ||
      candidate.available !== true ||
      candidate.bundleIdentifier !== 'ro.hont.tessera.host' ||
      typeof candidate.state !== 'string' ||
      !states.has(candidate.state)
    ) throw new Error('Mac Host status is invalid.')
    return {
      available: true,
      state: candidate.state as Exclude<MacHostStatus['state'], 'CLIENT_ONLY'>,
      bundleIdentifier: 'ro.hont.tessera.host',
    }
  }
}