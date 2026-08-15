import { describe, expect, it } from 'vitest'
import { createHttpClient, createRemoteApi } from '../src'

describe('Remote API', () => {
  it('uses owner-scoped encoded routes and version-bound idempotent mutations', async () => {
    const calls: Array<{ path: string; init: RequestInit }> = []
    const http = createHttpClient({
      createIdempotencyKey: (prefix) => `${prefix}-fixed`,
      send: async (path, init) => {
        calls.push({ path, init })
        const body = path.endsWith('/revoke')
          ? { host: { hostId: 'host/a' }, capabilities: [], capabilityGrants: [], resources: [], resourceGrants: [] }
          : { items: [], nextCursor: null }
        return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
      },
    })
    const remote = createRemoteApi(http)

    await remote.hosts()
    await remote.revokeHost({ hostId: 'host/a', version: 7 })

    expect(calls[0]).toMatchObject({ path: '/api/v1/hosts' })
    expect(calls[1].path).toBe('/api/v1/hosts/host%2Fa/revoke')
    expect(new Headers(calls[1].init.headers).get('Idempotency-Key')).toBe('host-revoke-fixed')
    expect(JSON.parse(String(calls[1].init.body))).toEqual({ expectedVersion: 7 })
  })

  it('binds confirmation grants and artifact paths without inventing client state', async () => {
    const calls: Array<{ path: string; init: RequestInit }> = []
    const http = createHttpClient({
      createIdempotencyKey: (prefix) => `${prefix}-fixed`,
      send: async (path, init) => {
        calls.push({ path, init })
        return new Response(JSON.stringify(path.includes('/confirm')
          ? { host: { hostId: 'host-1' }, capabilities: [], capabilityGrants: [], resources: [], resourceGrants: [] }
          : { artifact: { artifactId: 'artifact-1' }, textContent: 'plain' }), { status: 200 })
      },
    })
    const remote = createRemoteApi(http)

    await remote.confirmPairing('pairing/1', {
      expectedVersion: 2,
      confirmationCode: '123456',
      displayName: 'Home Mac',
      capabilityGrants: [{ capabilityId: 'host.repo.identity', capabilityVersion: '1' }],
      resourceGrants: [{ resourceId: 'repo-main', accessMode: 'READ_ONLY' }],
    })
    await remote.artifact('artifact/1')

    expect(calls[0].path).toBe('/api/v1/host-pairings/pairing%2F1/confirm')
    expect(JSON.parse(String(calls[0].init.body))).toMatchObject({ expectedVersion: 2, confirmationCode: '123456' })
    expect(calls[1].path).toBe('/api/v1/host-artifacts/artifact%2F1')
  })

  it('accepts the server ISSUED initial pairing state', async () => {
    const http = createHttpClient({
      createIdempotencyKey: () => 'host-pairing-fixed',
      send: async () => new Response(JSON.stringify({ pairingId: 'pairing-1', state: 'ISSUED', requestedHost: null, expiresAt: '2026-08-14T13:00:00Z', version: 1 }), { status: 201 }),
    })
    const pairing = await createRemoteApi(http).createPairing('a'.repeat(64))
    expect(pairing.state).toBe('ISSUED')
  })
})