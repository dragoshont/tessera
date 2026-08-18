import { app, safeStorage } from 'electron'
import { lstat, mkdir, readFile, rename, rm, writeFile, chmod } from 'node:fs/promises'
import path from 'node:path'
import { validateAuthState, type AuthState } from './security'

export interface PendingOidc {
  state: string
  verifier: string
  tokenEndpoint: string
  clientId: string
  scope: string
  expiresAt: string
}

interface SecureData {
  auth: AuthState
  pendingOidc: PendingOidc | null
}

const EMPTY: SecureData = { auth: null, pendingOidc: null }
const MAX_BYTES = 64 * 1024

export class AuthStore {
  private readonly file: string
  private queue: Promise<unknown> = Promise.resolve()

  constructor(file = path.join(app.getPath('userData'), 'secure-state.bin')) {
    this.file = file
  }

  async load(): Promise<SecureData> {
    return this.serial(async () => this.read())
  }

  async saveAuth(auth: AuthState): Promise<void> {
    await this.serial(async () => {
      const data = await this.read()
      await this.write({ ...data, auth: validateAuthState(auth, true) })
    })
  }

  async savePending(pendingOidc: PendingOidc | null): Promise<void> {
    await this.serial(async () => {
      const data = await this.read()
      await this.write({ ...data, pendingOidc })
    })
  }

  private async serial<T>(operation: () => Promise<T>): Promise<T> {
    const result = this.queue.then(operation, operation)
    this.queue = result.then(() => undefined, () => undefined)
    return result
  }

  private async read(): Promise<SecureData> {
    let info
    try {
      info = await lstat(this.file)
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') return { ...EMPTY }
      throw error
    }
    if (!info.isFile() || info.isSymbolicLink() || info.size > MAX_BYTES)
      throw new Error('Secure state file is invalid.')
    if (!safeStorage.isEncryptionAvailable()) throw new Error('macOS secure storage is unavailable.')
    const encrypted = await readFile(this.file)
    const parsed = JSON.parse(safeStorage.decryptString(encrypted)) as SecureData
    return {
      auth: validateAuthState(parsed.auth, true),
      pendingOidc: validatePending(parsed.pendingOidc),
    }
  }

  private async write(data: SecureData): Promise<void> {
    if (!safeStorage.isEncryptionAvailable()) throw new Error('macOS secure storage is unavailable.')
    await mkdir(path.dirname(this.file), { recursive: true, mode: 0o700 })
    const temporary = `${this.file}.${process.pid}.tmp`
    const encrypted = safeStorage.encryptString(JSON.stringify(data))
    if (encrypted.byteLength > MAX_BYTES) throw new Error('Secure state is too large.')
    await writeFile(temporary, encrypted, { mode: 0o600, flag: 'wx' })
    await chmod(temporary, 0o600)
    await rename(temporary, this.file)
    await chmod(this.file, 0o600)
    await rm(temporary, { force: true }).catch(() => undefined)
  }
}

function validatePending(value: unknown): PendingOidc | null {
  if (value === null || value === undefined) return null
  if (!value || typeof value !== 'object') throw new Error('Pending OIDC state is invalid.')
  const input = value as Record<string, unknown>
  for (const key of ['state', 'verifier', 'tokenEndpoint', 'clientId', 'scope', 'expiresAt'])
    if (typeof input[key] !== 'string') throw new Error('Pending OIDC state is incomplete.')
  const pending = input as unknown as PendingOidc
  if (pending.state.length > 256 || pending.verifier.length > 256 || new Date(pending.expiresAt) <= new Date())
    throw new Error('Pending OIDC state expired or invalid.')
  return pending
}
