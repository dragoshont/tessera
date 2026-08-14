import { describe, expect, it, vi } from 'vitest'
import { MacHostHelperManager } from '../src/host-helper'

describe('Mac Host lifecycle bridge', () => {
  it('keeps Desktop client-only when the helper is absent', async () => {
    const manager = new MacHostHelperManager('/definitely/not/a/helper')
    await expect(manager.status()).resolves.toEqual({
      available: false,
      state: 'CLIENT_ONLY',
      bundleIdentifier: 'ro.hont.tessera.host',
    })
    await expect(manager.setEnabled(false)).resolves.toMatchObject({ state: 'CLIENT_ONLY' })
    await expect(manager.setEnabled(true)).rejects.toThrow('not bundled')
  })

  it('invokes only fixed lifecycle verbs and validates bounded status', async () => {
    const execute = vi.fn(async (_file: string, args: readonly string[]) => ({
      stdout: JSON.stringify({ available: true, state: args[0] === 'register' ? 'ENABLED' : 'DISABLED', bundleIdentifier: 'ro.hont.tessera.host' }),
      stderr: '',
    }))
    const manager = new MacHostHelperManager(process.execPath, execute)
    await expect(manager.setEnabled(true)).resolves.toMatchObject({ state: 'ENABLED' })
    await expect(manager.setEnabled(false)).resolves.toMatchObject({ state: 'DISABLED' })
    expect(execute.mock.calls.map((call) => call[1])).toEqual([['register'], ['unregister']])
  })

  it('rejects unknown or expanded native status DTOs', async () => {
    const execute = vi.fn(async () => ({
      stdout: JSON.stringify({ available: true, state: 'ENABLED', bundleIdentifier: 'ro.hont.tessera.host', privateKey: 'no' }),
      stderr: '',
    }))
    const manager = new MacHostHelperManager(process.execPath, execute)
    await expect(manager.status()).rejects.toThrow('status is invalid')
  })
})