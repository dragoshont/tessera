import { describe, expect, it, vi } from 'vitest'
import { createHttpClient, GenerationFence, isAllowedAppPath, readBoundedJsonResponse, RouteManager, parseServerDescriptor } from '../src'

const id = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
const descriptor = { product: 'tessera', serverId: id, displayName: 'Tessera Home', serverVersion: '0.1.0', apiVersion: 'v1', protocolVersion: 1 }
const response = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

describe('server descriptor', () => {
  it('accepts only the exact bounded contract', () => {
    expect(parseServerDescriptor(descriptor)).toEqual(descriptor)
    expect(() => parseServerDescriptor({ ...descriptor, extra: true })).toThrow('server_descriptor_invalid')
    expect(() => parseServerDescriptor({ ...descriptor, protocolVersion: 2 })).toThrow('server_incompatible')
    expect(() => parseServerDescriptor({ ...descriptor, serverId: '00000000-0000-0000-0000-000000000000' })).toThrow('server_descriptor_invalid')
    expect(() => new RouteManager({ expectedServerId: '00000000-0000-0000-0000-000000000000', clientVersion: '1.0.0', routes: [{ kind: 'REMOTE', origin: 'https://remote.example', timeoutMs: 500 }] })).toThrow('expected_server_id_invalid')
  })
})

describe('verified route manager', () => {
  it('falls back without sending authentication to discovery', async () => {
    const fetcher = vi.fn<typeof fetch>()
      .mockRejectedValueOnce(new TypeError('offline'))
      .mockResolvedValueOnce(response(descriptor))
    const routes = new RouteManager({
      expectedServerId: id, clientVersion: '1.0.0', fetch: fetcher,
      routes: [
        { kind: 'LOCAL', origin: 'https://local.example', timeoutMs: 500 },
        { kind: 'REMOTE', origin: 'https://remote.example', timeoutMs: 500 },
      ],
    })
    await routes.connect()
    expect(routes.diagnostics.route).toBe('REMOTE')
    expect(fetcher).toHaveBeenCalledTimes(2)
    for (const call of fetcher.mock.calls) expect(new Headers(call[1]?.headers).has('Authorization')).toBe(false)
  })

  it('rejects a different server identity before an authenticated request', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(response({ ...descriptor, serverId: '11111111-1111-1111-1111-111111111111' }))
    const routes = new RouteManager({ expectedServerId: id, clientVersion: '1.0.0', fetch: fetcher, routes: [{ kind: 'REMOTE', origin: 'https://remote.example', timeoutMs: 500 }] })
    await expect(routes.request('/api/v1/accounts', {}, 'secret-token')).rejects.toThrow('server_identity_mismatch')
    expect(new Headers(fetcher.mock.calls[0][1]?.headers).has('Authorization')).toBe(false)
  })

  it('rejects an oversized descriptor before parsing it', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(new Response('x'.repeat(4097), { status: 200 }))
    const routes = new RouteManager({ expectedServerId: id, clientVersion: '1.0.0', fetch: fetcher, routes: [{ kind: 'REMOTE', origin: 'https://remote.example', timeoutMs: 500 }] })
    await expect(routes.connect()).rejects.toThrow('server_descriptor_too_large')
  })

  it('replays an idempotency-keyed mutation byte-for-byte only once', async () => {
    const fetcher = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(response(descriptor))
      .mockRejectedValueOnce(new TypeError('connection lost'))
      .mockResolvedValueOnce(response(descriptor))
      .mockResolvedValueOnce(response({ ok: true }))
    const routes = new RouteManager({
      expectedServerId: id, clientVersion: '1.0.0', fetch: fetcher,
      routes: [
        { kind: 'LOCAL', origin: 'https://local.example', timeoutMs: 500 },
        { kind: 'REMOTE', origin: 'https://remote.example', timeoutMs: 500 },
      ],
    })
    await routes.connect()
    const init = { method: 'POST', headers: { 'Idempotency-Key': 'message-1', 'Content-Type': 'application/json' }, body: '{"text":"hello"}' }
    const result = await routes.request('/api/v1/messages', init, 'token')
    expect(result.status).toBe(200)
    const calls = fetcher.mock.calls.filter((call) => String(call[0]).endsWith('/api/v1/messages'))
    expect(calls).toHaveLength(2)
    expect(calls[0][1]?.body).toBe(calls[1][1]?.body)
    expect(new Headers(calls[0][1]?.headers).get('Idempotency-Key')).toBe('message-1')
    expect(new Headers(calls[1][1]?.headers).get('Authorization')).toBe('Bearer token')
  })

  it('does not replay an ambiguous unkeyed mutation', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(response(descriptor)).mockRejectedValueOnce(new TypeError('connection lost'))
    const routes = new RouteManager({ expectedServerId: id, clientVersion: '1.0.0', fetch: fetcher, routes: [{ kind: 'REMOTE', origin: 'https://remote.example', timeoutMs: 500 }] })
    await routes.connect()
    await expect(routes.request('/api/v1/actions/a/approve', { method: 'POST', body: '{}' }, 'token')).rejects.toThrow('connection lost')
    expect(fetcher).toHaveBeenCalledTimes(2)
  })

  it('fails over a read after a transient gateway response', async () => {
    const fetcher = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(response(descriptor))
      .mockResolvedValueOnce(new Response(null, { status: 503 }))
      .mockResolvedValueOnce(response(descriptor))
      .mockResolvedValueOnce(response({ items: [] }))
    const routes = new RouteManager({ expectedServerId: id, clientVersion: '1.0.0', fetch: fetcher, routes: [{ kind: 'LOCAL', origin: 'https://local.example', timeoutMs: 500 }, { kind: 'REMOTE', origin: 'https://remote.example', timeoutMs: 500 }] })
    await routes.connect()
    expect((await routes.request('/api/v1/jobs')).status).toBe(200)
    expect(routes.diagnostics).toMatchObject({ state: 'DEGRADED', route: 'REMOTE', failureCode: 'route_failover' })
  })

  it('verifies the current route before acquiring or sending a token', async () => {
    const order: string[] = []
    const fetcher = vi.fn<typeof fetch>(async (url, init) => {
      const discovery = String(url).endsWith('/.well-known/tessera')
      order.push(discovery ? 'discovery' : 'api')
      if (discovery) {
        expect(new Headers(init?.headers).has('Authorization')).toBe(false)
        return response(descriptor)
      }
      expect(new Headers(init?.headers).get('Authorization')).toBe('Bearer secret-token')
      return response({ items: [], nextCursor: null })
    })
    const routes = new RouteManager({ expectedServerId: id, clientVersion: '1.0.0', fetch: fetcher, routes: [{ kind: 'REMOTE', origin: 'https://remote.example', timeoutMs: 500 }] })
    const client = createHttpClient({ routes, getAccessToken: async () => { order.push('token'); return 'secret-token' } })
    await client.request('/accounts')
    expect(order).toEqual(['discovery', 'token', 'api'])
  })

  it('rejects a response body that completes after the session lease is invalidated', async () => {
    const fence = new GenerationFence()
    let bodyController!: ReadableStreamDefaultController<Uint8Array>
    const body = new ReadableStream<Uint8Array>({ start(controller) { bodyController = controller } })
    const fetcher = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(response(descriptor))
      .mockResolvedValueOnce(new Response(body, { status: 200, headers: { 'Content-Type': 'application/json' } }))
    const routes = new RouteManager({ expectedServerId: id, clientVersion: '1.0.0', fetch: fetcher, routes: [{ kind: 'REMOTE', origin: 'https://remote.example', timeoutMs: 500 }] })
    const generation = fence.capture()
    const client = createHttpClient({ routes, getAuthLease: async () => ({ accessToken: 'secret-token', isCurrent: () => fence.isCurrent(generation) }) })
    const request = client.request('/accounts')
    await vi.waitFor(() => expect(fetcher).toHaveBeenCalledTimes(2))
    fence.invalidate()
    bodyController.enqueue(new TextEncoder().encode('{"items":[],"nextCursor":null}'))
    bodyController.close()
    await expect(request).rejects.toThrow('session_invalidated')
  })

  it('requires fresh verification after network invalidation', async () => {
    const fetcher = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(response(descriptor))
      .mockResolvedValueOnce(response(descriptor))
      .mockResolvedValueOnce(response({ items: [] }))
    const routes = new RouteManager({ expectedServerId: id, clientVersion: '1.0.0', fetch: fetcher, routes: [{ kind: 'REMOTE', origin: 'https://remote.example', timeoutMs: 500 }] })
    await routes.connect()
    routes.invalidate('network_changed')
    await routes.request('/api/v1/jobs')
    expect(fetcher.mock.calls.filter((call) => String(call[0]).endsWith('/.well-known/tessera'))).toHaveLength(2)
  })

  it('does not let a stale in-flight probe overwrite a post-transition route', async () => {
    let resolveStale!: (value: Response) => void
    const staleResponse = new Promise<Response>((resolve) => { resolveStale = resolve })
    const fetcher = vi.fn<typeof fetch>().mockImplementationOnce(async () => staleResponse).mockResolvedValueOnce(response(descriptor))
    const routes = new RouteManager({ expectedServerId: id, clientVersion: '1.0.0', fetch: fetcher, routes: [{ kind: 'REMOTE', origin: 'https://remote.example', timeoutMs: 500 }] })
    const stale = routes.connect()
    routes.invalidate('network_changed')
    const fresh = routes.connect()
    resolveStale(response(descriptor))
    await expect(stale).rejects.toThrow('route_invalidated')
    await expect(fresh).resolves.toEqual(descriptor)
    expect(routes.diagnostics.state).toBe('CONNECTED')
  })

  it('does not send authentication after invalidation during a failover probe', async () => {
    let resolveAlternate!: (value: Response) => void
    const alternateProbe = new Promise<Response>((resolve) => { resolveAlternate = resolve })
    const fetcher = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(response(descriptor))
      .mockRejectedValueOnce(new TypeError('local disconnected'))
      .mockImplementationOnce(async () => alternateProbe)
    const routes = new RouteManager({ expectedServerId: id, clientVersion: '1.0.0', fetch: fetcher, routes: [{ kind: 'LOCAL', origin: 'https://local.example', timeoutMs: 500 }, { kind: 'REMOTE', origin: 'https://remote.example', timeoutMs: 500 }] })
    await routes.connect()
    const request = routes.request('/api/v1/jobs', {}, 'secret-token')
    await vi.waitFor(() => expect(fetcher).toHaveBeenCalledTimes(3))
    routes.invalidate('network_changed')
    resolveAlternate(response(descriptor))
    await expect(request).rejects.toThrow('route_invalidated')
    expect(fetcher.mock.calls.filter((call) => String(call[0]).endsWith('/api/v1/jobs'))).toHaveLength(1)
  })

  it('rejects an authenticated response that completes after route invalidation', async () => {
    let resolveApi!: (value: Response) => void
    const apiResponse = new Promise<Response>((resolve) => { resolveApi = resolve })
    const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(response(descriptor)).mockImplementationOnce(async () => apiResponse)
    const routes = new RouteManager({ expectedServerId: id, clientVersion: '1.0.0', fetch: fetcher, routes: [{ kind: 'REMOTE', origin: 'https://remote.example', timeoutMs: 500 }] })
    const request = routes.request('/api/v1/jobs', {}, 'secret-token')
    await vi.waitFor(() => expect(fetcher).toHaveBeenCalledTimes(2))
    routes.invalidate('network_changed')
    resolveApi(response({ items: [] }))
    await expect(request).rejects.toThrow('route_invalidated')
  })
})

describe('injected HTTP transport', () => {
  it('preserves browser-owned auth headers and API prefix', async () => {
    const send = vi.fn(async () => response({ items: [], nextCursor: null }))
    const client = createHttpClient({ send })
    await client.request('/accounts', { headers: { 'X-Tessera-Dev-Principal': 'alice@example.com' } })
    expect(send).toHaveBeenCalledWith('/api/v1/accounts', expect.objectContaining({ headers: { 'X-Tessera-Dev-Principal': 'alice@example.com' } }))
  })

  it('rejects an oversized API response', async () => {
    const client = createHttpClient({ send: async () => new Response(JSON.stringify({ value: 'x'.repeat(2 * 1024 * 1024) }), { status: 200 }) })
    await expect(client.request('/settings')).rejects.toThrow('response_too_large')
  })
})

describe('native navigation allowlist', () => {
  it('accepts product routes and rejects traversal or arbitrary destinations', () => {
    expect(isAllowedAppPath('/(tabs)/more')).toBe(true)
    expect(isAllowedAppPath('/action/action_123')).toBe(true)
    expect(isAllowedAppPath('/settings?token=value')).toBe(false)
    expect(isAllowedAppPath('/action/../settings')).toBe(false)
    expect(isAllowedAppPath('https://attacker.example')).toBe(false)
  })
})

describe('session generation fence', () => {
  it('invalidates in-flight work before it can restore signed-out state', () => {
    const fence = new GenerationFence()
    const refreshGeneration = fence.capture()
    expect(fence.isCurrent(refreshGeneration)).toBe(true)
    fence.invalidate()
    expect(fence.isCurrent(refreshGeneration)).toBe(false)
  })

  it('marks an older rejection stale after a newer same-resource load succeeds', async () => {
    const fence = new GenerationFence()
    let rejectOlder!: (error: Error) => void
    let resolveNewer!: (value: string) => void
    const olderOperation = new Promise<string>((_, reject) => { rejectOlder = reject })
    const newerOperation = new Promise<string>((resolve) => { resolveNewer = resolve })
    const older = fence.runLatest(() => olderOperation)
    const newer = fence.runLatest(() => newerOperation)
    resolveNewer('current')
    rejectOlder(new Error('stale failure'))
    await expect(newer).resolves.toEqual({ current: true, value: 'current' })
    await expect(older).resolves.toMatchObject({ current: false, error: expect.any(Error) })
  })
})

describe('bounded response reader', () => {
  it('times out a stalled response body', async () => {
    let canceled = false
    const stalled = new Response(new ReadableStream({ start() {}, cancel() { canceled = true } }), { status: 200 })
    await expect(readBoundedJsonResponse(stalled, 1024, 10)).rejects.toThrow('response_timeout')
    expect(canceled).toBe(true)
  })
})