import { describe, expect, it } from 'vitest'
import {
  API_ORIGIN,
  assertRendererUrl,
  parseDeepLink,
  validateAuthState,
  validateExternalUrl,
  validateNotification,
  validateOidcInput,
  validateRoute,
} from '../src/security'

describe('desktop trust boundary', () => {
  it('pins the production API and renderer origin', () => {
    expect(API_ORIGIN).toBe('https://tessera.hont.ro')
    expect(() => assertRendererUrl('app://tessera/chat')).not.toThrow()
    for (const url of ['https://tessera.hont.ro/chat', 'app://evil/chat', 'file:///tmp/index.html'])
      expect(() => assertRendererUrl(url)).toThrow()
  })

  it('allows only reviewed external origins without URL confusion', () => {
    expect(validateExternalUrl('https://accounts.google.com/o/oauth2/v2/auth')).toContain('accounts.google.com')
    expect(validateExternalUrl('https://auth.hont.ro/application/o/authorize/')).toContain('auth.hont.ro')
    for (const url of [
      'http://auth.hont.ro/',
      'https://user@auth.hont.ro/',
      'https://auth.hont.ro:444/',
      'https://127.0.0.1/',
      'https://auth.hont.ro.evil.example/',
      'https://evil.example/',
    ]) expect(() => validateExternalUrl(url)).toThrow()
  })

  it('rejects privileged or malformed routes and deep links', () => {
    expect(() => validateRoute('/actions')).toThrow()
    expect(parseDeepLink('tessera://open/jobs')).toEqual({ kind: 'navigate', route: '/jobs' })
    expect(parseDeepLink('tessera://auth/callback?code=x&state=y').kind).toBe('auth')
    for (const link of [
      'tessera://open/chat?capability=send',
      'tessera://open/../settings',
      'tessera://execute/action',
      'https://tessera.hont.ro/chat',
    ]) expect(() => parseDeepLink(link)).toThrow()
  })

  it('never lets renderer IPC persist an OIDC token', () => {
    expect(validateAuthState(null, false)).toBeNull()
    expect(validateAuthState({ kind: 'dev', principal: 'alice@example.com' }, false)).toEqual({ kind: 'dev', principal: 'alice@example.com' })
    expect(() => validateAuthState({ kind: 'oidc', token: 'a'.repeat(100) }, false)).toThrow()
    expect(validateAuthState({ kind: 'oidc', token: 'a'.repeat(100) }, true)).toEqual({ kind: 'oidc', token: 'a'.repeat(100) })
  })

  it('bounds OIDC and notification payloads', () => {
    expect(validateOidcInput({ authority: 'https://auth.hont.ro/application/o/tessera/', clientId: 'desktop-client', scope: 'openid profile email' })).toMatchObject({ clientId: 'desktop-client' })
    expect(() => validateOidcInput({ authority: 'https://evil.example/', clientId: 'x', scope: 'openid' })).toThrow()
    expect(validateNotification({ title: 'Action pending', body: 'Review it', route: '/activity' }).route).toBe('/activity')
    expect(() => validateNotification({ title: 'x', body: 'y', route: '/not-real' })).toThrow()
  })

  it('exposes no renderer operation for signing, paths, envelopes, or execution', async () => {
    const preload = await import('node:fs/promises').then(({ readFile }) => readFile(new URL('../src/preload.ts', import.meta.url), 'utf8'))
    expect(preload).toContain('getMacHostStatus')
    expect(preload).toContain('setMacHostEnabled')
    expect(preload).not.toMatch(/private.?key|sign\(|readPath|sendEnvelope|executeHost|pairHost/i)
  })
})
