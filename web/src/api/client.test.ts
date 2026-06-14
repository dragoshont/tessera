import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createHttpClient, createInMemoryClient } from './client'
import { demoLiveViewHandle } from '../data/fixtures'
import type { LiveViewHandle } from '../data/types'

// A minimal Response-like stub so the client tests don't depend on the runtime's
// global Response/fetch implementation.
function makeResponse({ status, body }: { status: number; body: unknown }): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
    text: async () => (typeof body === 'string' ? body : JSON.stringify(body)),
  } as unknown as Response
}

const originalFetch = globalThis.fetch
let fetchMock: ReturnType<typeof vi.fn>

beforeEach(() => {
  fetchMock = vi.fn()
  globalThis.fetch = fetchMock as unknown as typeof fetch
})

afterEach(() => {
  globalThis.fetch = originalFetch
  vi.restoreAllMocks()
})

describe('createHttpClient — requestLiveView', () => {
  it('maps the fail-closed 503 to { unavailable } with the broker reason (no throw)', async () => {
    const reason = 'live hand-off is not configured: no browser worker is wired (fail-closed).'
    fetchMock.mockResolvedValueOnce(makeResponse({ status: 503, body: { error: reason } }))

    const client = createHttpClient({ baseUrl: 'http://broker.test/api' })
    const result = await client.requestLiveView('health:alice@example.com')

    expect(result).toEqual({ unavailable: reason })
    // The connectionId (with ':' and '@') is URL-encoded into the POST path.
    expect(fetchMock).toHaveBeenCalledWith(
      'http://broker.test/api/portal/connections/health%3Aalice%40example.com/live-view',
      expect.objectContaining({ method: 'POST' }),
    )
  })

  it('maps a 200 to { handle }', async () => {
    const handle: LiveViewHandle = {
      liveViewUrl: 'https://worker.test/s/abc',
      mode: 'readwrite',
      sessionTtlSeconds: 300,
      expiresAt: new Date(Date.now() + 300_000).toISOString(),
      targetHostname: 'portal.example-health.com',
    }
    fetchMock.mockResolvedValueOnce(makeResponse({ status: 200, body: handle }))

    const client = createHttpClient({ baseUrl: 'http://broker.test/api' })
    const result = await client.requestLiveView('health:alice@example.com')

    expect(result).toEqual({ handle })
  })

  it('sends auth headers from getAuthHeader', async () => {
    fetchMock.mockResolvedValueOnce(makeResponse({ status: 503, body: { error: 'nope' } }))
    const client = createHttpClient({
      baseUrl: 'http://broker.test/api',
      getAuthHeader: () => ({ 'X-Tessera-Dev-Principal': 'alice@example.com' }),
    })

    await client.requestLiveView('health:alice@example.com')

    const [, init] = fetchMock.mock.calls[0]
    expect((init as RequestInit).headers).toMatchObject({
      'X-Tessera-Dev-Principal': 'alice@example.com',
    })
  })
})

describe('createHttpClient — listPeople', () => {
  it('maps a 403 (member) to an empty list, keeping the UI calm', async () => {
    fetchMock.mockResolvedValueOnce(makeResponse({ status: 403, body: { error: 'forbidden' } }))
    const client = createHttpClient({ baseUrl: 'http://broker.test/api' })

    await expect(client.listPeople()).resolves.toEqual([])
  })
})

describe('createInMemoryClient — requestLiveView', () => {
  it('is fail-closed by default', async () => {
    const client = createInMemoryClient()
    const result = await client.requestLiveView('conn-alice-health')

    expect(result).toEqual({ unavailable: expect.stringContaining('fail-closed') })
  })

  it('returns the seeded handle when a liveView seed is provided', async () => {
    const client = createInMemoryClient({ liveView: () => ({ handle: demoLiveViewHandle }) })
    const result = await client.requestLiveView('conn-alice-health')

    expect(result).toEqual({ handle: demoLiveViewHandle })
  })
})

describe('createHttpClient — config + recipes', () => {
  it('GETs /portal/config', async () => {
    const config = { authMode: 'dev', devLoopback: true }
    fetchMock.mockResolvedValueOnce(makeResponse({ status: 200, body: config }))
    const client = createHttpClient({ baseUrl: 'http://broker.test/api' })

    await expect(client.getConfig()).resolves.toEqual(config)
    expect(fetchMock).toHaveBeenCalledWith(
      'http://broker.test/api/portal/config',
      expect.objectContaining({ method: 'GET' }),
    )
  })

  it('GETs /portal/recipes', async () => {
    const recipes = [{ provider: 'health', displayName: 'Health Portal' }]
    fetchMock.mockResolvedValueOnce(makeResponse({ status: 200, body: recipes }))
    const client = createHttpClient({ baseUrl: 'http://broker.test/api' })

    await expect(client.listRecipes()).resolves.toEqual(recipes)
  })
})

describe('createHttpClient — createConnection', () => {
  it('POSTs the binding and returns the 201 connection', async () => {
    const connection = {
      connectionId: 'health:bob@example.com',
      ownerPrincipal: 'bob@example.com',
      provider: 'health',
      displayName: 'Health Portal',
      status: 'absent',
      expiryIsEstimated: false,
      hasCookies: false,
      hasRefreshToken: false,
      hasAccessToken: false,
    }
    fetchMock.mockResolvedValueOnce(makeResponse({ status: 201, body: connection }))
    const client = createHttpClient({
      baseUrl: 'http://broker.test/api',
      getAuthHeader: () => ({ 'X-Tessera-Dev-Principal': 'alice@example.com' }),
    })

    const input = { provider: 'health', principal: 'bob@example.com', credential: 'health-bob-session' }
    await expect(client.createConnection(input)).resolves.toEqual(connection)

    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('http://broker.test/api/portal/connections')
    expect((init as RequestInit).method).toBe('POST')
    expect((init as RequestInit).body).toBe(JSON.stringify(input))
    expect((init as RequestInit).headers).toMatchObject({
      'Content-Type': 'application/json',
      'X-Tessera-Dev-Principal': 'alice@example.com',
    })
  })

  it('throws HttpError(403) when the caller may not add for that person', async () => {
    fetchMock.mockResolvedValueOnce(makeResponse({ status: 403, body: { error: 'forbidden' } }))
    const client = createHttpClient({ baseUrl: 'http://broker.test/api' })

    await expect(
      client.createConnection({ provider: 'health', principal: 'x@example.com', credential: 'n' }),
    ).rejects.toMatchObject({ status: 403 })
  })
})

describe('createInMemoryClient — createConnection', () => {
  it('appends the new binding so later listConnections reflects it (Absent)', async () => {
    const client = createInMemoryClient()
    const created = await client.createConnection({
      provider: 'health',
      principal: 'dave@example.com',
      credential: 'health-dave-session',
    })

    expect(created.connectionId).toBe('health:dave@example.com')
    expect(created.status).toBe('absent')
    // The credential NAME is never echoed back as a value — presence only.
    expect(Object.values(created)).not.toContain('health-dave-session')

    const owned = await client.listConnections('dave@example.com')
    expect(owned.map((connection) => connection.connectionId)).toContain('health:dave@example.com')
  })
})

describe('createInMemoryClient — awareness dashboard (ADR 0017)', () => {
  it('getActivity self-scopes to a principal and summarizes the whole window', async () => {
    const client = createInMemoryClient()

    const self = await client.getActivity('alice@example.com')
    // Only alice's rows; none of bob's leak in.
    expect(self.entries.every((row) => row.onBehalfOf === 'alice@example.com')).toBe(true)
    // The summary spans the scoped window and counts effects.
    expect(self.summary.total).toBe(self.entries.length)
    expect(self.summary.allow + self.summary.deny + self.summary.stepUp).toBe(self.summary.total)
  })

  it('getActivity returns everyone when no principal is given, newest-first, capped by limit', async () => {
    const client = createInMemoryClient()

    const all = await client.getActivity(undefined, 2)
    expect(all.entries).toHaveLength(2)
    // newest-first
    expect(all.entries[0].timestamp >= all.entries[1].timestamp).toBe(true)
    // summary still spans the full window (more than the 2 shown)
    expect(all.summary.total).toBeGreaterThan(2)
  })

  it('listDelegations self-scopes; the all-view includes pure automation', async () => {
    const client = createInMemoryClient()

    const mine = await client.listDelegations('bob@example.com')
    expect(mine.every((d) => d.onBehalfOf === 'bob@example.com')).toBe(true)

    const everyone = await client.listDelegations()
    expect(everyone.some((d) => d.isAutomation && d.onBehalfOf === null)).toBe(true)
  })

  it('listModules exposes egress posture without any upstream path', async () => {
    const client = createInMemoryClient()
    const modules = await client.listModules()

    const statusOnly = modules.find((m) => m.egress === 'none')
    expect(statusOnly?.upstreamHost).toBeNull()
    // host only — never a full URL/path
    expect(JSON.stringify(modules)).not.toContain('/v1')
  })

  it('getSchedule defaults to an honest "none" owner with no faked run times', async () => {
    const client = createInMemoryClient()
    const schedule = await client.getSchedule('health:carol@example.com')

    expect(schedule.rotationOwner).toBe('none')
    expect(schedule.refreshConfigured).toBe(false)
    expect(schedule.lastRotatedAt).toBeNull()
    expect(schedule.nextRotationAt).toBeNull()
  })
})

describe('createHttpClient — awareness dashboard (ADR 0017)', () => {
  it('GETs /portal/audit with principal + limit query params', async () => {
    const feed = { entries: [], summary: { total: 0, allow: 0, deny: 0, stepUp: 0, byTarget: {}, byCaller: {}, since: null, until: null } }
    fetchMock.mockResolvedValueOnce(makeResponse({ status: 200, body: feed }))
    const client = createHttpClient({ baseUrl: 'http://broker.test/api' })

    await client.getActivity('alice@example.com', 50)
    expect(fetchMock).toHaveBeenCalledWith(
      'http://broker.test/api/portal/audit?principal=alice%40example.com&limit=50',
      expect.objectContaining({ method: 'GET' }),
    )
  })

  it('GETs /portal/delegations (self when no principal)', async () => {
    fetchMock.mockResolvedValueOnce(makeResponse({ status: 200, body: [] }))
    const client = createHttpClient({ baseUrl: 'http://broker.test/api' })

    await client.listDelegations()
    expect(fetchMock).toHaveBeenCalledWith(
      'http://broker.test/api/portal/delegations',
      expect.objectContaining({ method: 'GET' }),
    )
  })

  it('GETs the schedule with the connection id URL-encoded', async () => {
    const schedule = { connectionId: 'health:alice@example.com', rotationOwner: 'external', refreshConfigured: true, detail: '', lastRotatedAt: null, nextRotationAt: null }
    fetchMock.mockResolvedValueOnce(makeResponse({ status: 200, body: schedule }))
    const client = createHttpClient({ baseUrl: 'http://broker.test/api' })

    await client.getSchedule('health:alice@example.com')
    expect(fetchMock).toHaveBeenCalledWith(
      'http://broker.test/api/portal/connections/health%3Aalice%40example.com/schedule',
      expect.objectContaining({ method: 'GET' }),
    )
  })
})
