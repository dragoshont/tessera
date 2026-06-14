import type {
  AuditFeed,
  AuditRow,
  Connection,
  CreateConnectionInput,
  Delegation,
  LiveViewHandle,
  LiveViewResult,
  Module,
  Person,
  PortalConfig,
  Recipe,
  Role,
  Schedule,
} from '../data/types'
import {
  auditRows as fixtureAuditRows,
  connections as fixtureConnections,
  currentUserPrincipal as fixtureCurrentUserPrincipal,
  delegations as fixtureDelegations,
  modules as fixtureModules,
  people as fixturePeople,
  portalConfigDev,
  recipes as fixtureRecipes,
} from '../data/fixtures'
import { authHeader } from '../app/auth'

// The portal talks to the broker through this narrow, typed surface only.
// `createHttpClient` hits the real .NET broker portal endpoints; the in-memory
// `createInMemoryClient` runs the same contract over fixtures for Storybook and
// tests. Which one the running app uses is decided by `tesseraClient` below.
export interface TesseraClient {
  /** Sign-in configuration (`GET /portal/config`) — fetched first to pick a flow. */
  getConfig(): Promise<PortalConfig>
  getCurrentUser(): Promise<Person>
  listPeople(): Promise<Person[]>
  listConnections(ownerPrincipal?: string): Promise<Connection[]>
  getConnection(connectionId: string): Promise<Connection | undefined>
  /** The connect wizard's provider picker (`GET /portal/recipes`). */
  listRecipes(): Promise<Recipe[]>
  /** The connect wizard's write (`POST /portal/connections`) → the new binding. */
  createConnection(input: CreateConnectionInput): Promise<Connection>
  /**
   * Ask the broker to mint a short-TTL Live hand-off handle for one connection.
   * The fail-closed default (no worker wired) returns `{ unavailable }` — the UI
   * shows a calm "not set up yet" explainer, never an error spinner.
   */
  requestLiveView(connectionId: string): Promise<LiveViewResult>
  /**
   * The secret-free activity feed (`GET /portal/audit`) — newest-first rows + a
   * window summary. Self-scoped by default; an operator may pass a principal, or
   * omit it for everyone. `limit` caps the rows (the summary still spans the window).
   */
  getActivity(principal?: string, limit?: number): Promise<AuditFeed>
  /**
   * Who/what may act as a person (`GET /portal/delegations`). Self by default; an
   * operator may pass a principal, or omit it for every grant (incl. automation).
   */
  listDelegations(principal?: string): Promise<Delegation[]>
  /** The loaded connector catalog + egress posture (`GET /portal/modules`). */
  listModules(): Promise<Module[]>
  /** One connection's rotation schedule (`GET /portal/connections/{id}/schedule`). */
  getSchedule(connectionId: string): Promise<Schedule>
}

export interface InMemorySeed {
  config?: PortalConfig
  people?: Person[]
  connections?: Connection[]
  recipes?: Recipe[]
  currentUserPrincipal?: string
  /** Drive the Live hand-off in stories/tests. Defaults to fail-closed Unavailable. */
  liveView?: (connectionId: string) => LiveViewResult
  /** Awareness-feed rows (ADR 0017); the in-memory client scopes + summarizes them. */
  auditRows?: AuditRow[]
  delegations?: Delegation[]
  modules?: Module[]
  /** Per-connection schedule override; defaults to a synthesized "none" schedule. */
  schedules?: Record<string, Schedule>
}

const adminsFirst = (a: Person, b: Person): number => {
  if (a.role !== b.role) return a.role === 'Admin' ? -1 : 1
  return a.principal.localeCompare(b.principal)
}

const FAIL_CLOSED_REASON = 'live hand-off is not configured (fail-closed)'

/** Builds the activity-feed summary over a set of rows (mirrors the broker rollup). */
function summarizeAudit(rows: AuditRow[]): AuditFeed['summary'] {
  const byTarget: Record<string, number> = {}
  const byCaller: Record<string, number> = {}
  let allow = 0
  let deny = 0
  let stepUp = 0
  let since: string | null = null
  let until: string | null = null
  for (const row of rows) {
    if (row.effect === 'allow') allow += 1
    else if (row.effect === 'step-up') stepUp += 1
    else deny += 1
    byTarget[row.target] = (byTarget[row.target] ?? 0) + 1
    byCaller[row.caller] = (byCaller[row.caller] ?? 0) + 1
    if (since === null || row.timestamp < since) since = row.timestamp
    if (until === null || row.timestamp > until) until = row.timestamp
  }
  return { total: rows.length, allow, deny, stepUp, byTarget, byCaller, since, until }
}

/** Build a client over the given seed (defaults to the shipped fixtures). Tests
 *  and stories inject their own seed to exercise empty / mixed states. */
export function createInMemoryClient(seed: InMemorySeed = {}): TesseraClient {
  const people = seed.people ?? fixturePeople
  // A mutable copy so createConnection is reflected by later listConnections calls
  // (the connect wizard can demo end-to-end on fixtures).
  const connections = [...(seed.connections ?? fixtureConnections)]
  const recipes = seed.recipes ?? fixtureRecipes
  const config = seed.config ?? portalConfigDev
  const currentPrincipal = seed.currentUserPrincipal ?? fixtureCurrentUserPrincipal

  return {
    async getConfig() {
      return config
    },
    async getCurrentUser() {
      return people.find((person) => person.principal === currentPrincipal) ?? people[0]
    },
    async listPeople() {
      return [...people].sort(adminsFirst)
    },
    async listConnections(ownerPrincipal) {
      return connections.filter(
        (connection) => !ownerPrincipal || connection.ownerPrincipal === ownerPrincipal,
      )
    },
    async getConnection(connectionId) {
      return connections.find((connection) => connection.connectionId === connectionId)
    },
    async listRecipes() {
      return [...recipes]
    },
    async createConnection({ provider, principal, credential }) {
      // The broker would return the full projection; here we synthesize an honest
      // one: a fresh binding is Absent (no session seeded yet). `credential` (the
      // store secret NAME) is never echoed back — presence only, never the value.
      void credential
      const recipe = recipes.find((entry) => entry.provider === provider)
      const label = recipe?.displayName ?? provider
      const connection: Connection = {
        connectionId: `${provider}:${principal}`,
        ownerPrincipal: principal,
        provider: label,
        displayName: label,
        status: 'absent',
        expiryIsEstimated: false,
        hasCookies: false,
        hasRefreshToken: false,
        hasAccessToken: false,
      }
      const existing = connections.findIndex((c) => c.connectionId === connection.connectionId)
      if (existing >= 0) connections[existing] = connection
      else connections.push(connection)
      return connection
    },
    async requestLiveView(connectionId) {
      // Default is fail-closed: deploying the portal opens no remote browser until a
      // worker adapter is wired. Stories/tests opt into the happy path via `liveView`.
      return seed.liveView?.(connectionId) ?? { unavailable: FAIL_CLOSED_REASON }
    },
    async getActivity(principal, limit) {
      const rows = seed.auditRows ?? fixtureAuditRows
      // Self-scope when a principal is given (mirrors the broker), newest-first.
      const scoped = principal
        ? rows.filter((row) => row.onBehalfOf?.toLowerCase() === principal.toLowerCase())
        : rows
      const ordered = [...scoped].sort((a, b) => b.timestamp.localeCompare(a.timestamp))
      const entries = typeof limit === 'number' ? ordered.slice(0, limit) : ordered
      // The summary spans the whole scoped window, not just the shown rows.
      return { entries, summary: summarizeAudit(ordered) }
    },
    async listDelegations(principal) {
      const all = seed.delegations ?? fixtureDelegations
      return principal
        ? all.filter((d) => d.onBehalfOf?.toLowerCase() === principal.toLowerCase())
        : [...all]
    },
    async listModules() {
      return [...(seed.modules ?? fixtureModules)]
    },
    async getSchedule(connectionId) {
      return (
        seed.schedules?.[connectionId] ?? {
          connectionId,
          rotationOwner: 'none',
          refreshConfigured: false,
          detail: 'No automatic rotation — this session is static and is re-seeded by hand.',
          lastRotatedAt: null,
          nextRotationAt: null,
        }
      )
    },
  }
}

/** A non-2xx response from the broker (carries the status so callers can branch). */
export class HttpError extends Error {
  readonly status: number

  constructor(status: number, message?: string) {
    super(message && message.length > 0 ? message : `HTTP ${status}`)
    this.name = 'HttpError'
    this.status = status
  }
}

async function safeText(response: Response): Promise<string> {
  try {
    return await response.text()
  } catch {
    return ''
  }
}

async function safeJson<T>(response: Response): Promise<T | undefined> {
  try {
    return (await response.json()) as T
  } catch {
    return undefined
  }
}

export interface HttpClientOptions {
  baseUrl: string
  /** Returns auth headers per request (e.g. a verified `Authorization: Bearer …`,
   *  or the loopback `X-Tessera-Dev-Principal`). Kept as a callback so tokens are
   *  never baked into frontend code. */
  getAuthHeader?: () => Record<string, string>
}

/**
 * An HTTP `TesseraClient` over the real .NET broker portal endpoints (camelCase
 * JSON). Mirrors the in-memory client's contract exactly, so the views are
 * agnostic to which one is wired.
 */
export function createHttpClient({ baseUrl, getAuthHeader }: HttpClientOptions): TesseraClient {
  const base = baseUrl.replace(/\/+$/, '')
  const authHeaders = (): Record<string, string> => getAuthHeader?.() ?? {}

  async function getJson<T>(path: string): Promise<T> {
    const response = await fetch(`${base}${path}`, {
      method: 'GET',
      headers: { Accept: 'application/json', ...authHeaders() },
    })
    if (!response.ok) throw new HttpError(response.status, await safeText(response))
    return (await response.json()) as T
  }

  async function postJson<T>(path: string, body: unknown): Promise<T> {
    const response = await fetch(`${base}${path}`, {
      method: 'POST',
      headers: { Accept: 'application/json', 'Content-Type': 'application/json', ...authHeaders() },
      body: JSON.stringify(body),
    })
    if (!response.ok) throw new HttpError(response.status, await safeText(response))
    return (await response.json()) as T
  }

  // The broker keys a connection as "{provider}:{principal}", so the owner can be
  // recovered from the id to scope the (list-only) read used by getConnection.
  function ownerFromConnectionId(connectionId: string): string | undefined {
    const separator = connectionId.indexOf(':')
    return separator >= 0 ? connectionId.slice(separator + 1) : undefined
  }

  async function listConnections(ownerPrincipal?: string): Promise<Connection[]> {
    const query = ownerPrincipal ? `?principal=${encodeURIComponent(ownerPrincipal)}` : ''
    return getJson<Connection[]>(`/portal/connections${query}`)
  }

  return {
    async getConfig() {
      return getJson<PortalConfig>('/portal/config')
    },
    async getCurrentUser() {
      // /portal/me carries identity only; the per-person counts are a separate
      // projection members can't read. Default to 0 — the shell keys off principal
      // and role (admin-nav gating, identity chip), not these counts.
      const me = await getJson<{ principal: string; role: Role }>('/portal/me')
      return { principal: me.principal, role: me.role, connectionCount: 0, needsAttentionCount: 0 }
    },
    async listPeople() {
      try {
        return await getJson<Person[]>('/portal/people')
      } catch (error) {
        // Members get 403 here — keep the UI calm: they simply have no people list.
        if (error instanceof HttpError && error.status === 403) return []
        throw error
      }
    },
    listConnections,
    async getConnection(connectionId) {
      const owner = ownerFromConnectionId(connectionId)
      const list = await listConnections(owner)
      return list.find((connection) => connection.connectionId === connectionId)
    },
    async listRecipes() {
      return getJson<Recipe[]>('/portal/recipes')
    },
    async createConnection(input) {
      // 201 returns the new binding. A 403 (not allowed to add for that person) or
      // 400 (bad input) throws HttpError so the wizard can branch on .status.
      return postJson<Connection>('/portal/connections', input)
    },
    async requestLiveView(connectionId) {
      const response = await fetch(
        `${base}/portal/connections/${encodeURIComponent(connectionId)}/live-view`,
        { method: 'POST', headers: { Accept: 'application/json', ...authHeaders() } },
      )
      // 503 is the fail-closed default (no worker wired) — a calm Unavailable, not
      // an error. Surface the broker's secret-free reason verbatim.
      if (response.status === 503) {
        const body = await safeJson<{ error?: string }>(response)
        return { unavailable: body?.error ?? FAIL_CLOSED_REASON }
      }
      if (!response.ok) throw new HttpError(response.status, await safeText(response))
      const handle = (await response.json()) as LiveViewHandle
      return { handle }
    },
    async getActivity(principal, limit) {
      const params = new URLSearchParams()
      if (principal) params.set('principal', principal)
      if (typeof limit === 'number') params.set('limit', String(limit))
      const query = params.toString()
      return getJson<AuditFeed>(`/portal/audit${query ? `?${query}` : ''}`)
    },
    async listDelegations(principal) {
      const query = principal ? `?principal=${encodeURIComponent(principal)}` : ''
      return getJson<Delegation[]>(`/portal/delegations${query}`)
    },
    async listModules() {
      return getJson<Module[]>('/portal/modules')
    },
    async getSchedule(connectionId) {
      return getJson<Schedule>(
        `/portal/connections/${encodeURIComponent(connectionId)}/schedule`,
      )
    },
  }
}

const apiUrl = import.meta.env.VITE_TESSERA_API_URL
const demoMode = import.meta.env.VITE_TESSERA_DEMO === '1'

/**
 * The app's client. By default it talks to the REAL .NET broker over HTTP at the
 * same origin that serves the SPA (`baseUrl: ''`) — `VITE_TESSERA_API_URL` is an
 * optional override for `npm run dev` against a separately-running broker (e.g.
 * http://127.0.0.1:8080). Per-request auth flows through `authHeader()` (the dev
 * principal header or a verified `Authorization: Bearer …`), never a baked-in token.
 *
 * `VITE_TESSERA_DEMO=1` swaps in the in-memory fixtures client for a no-backend
 * preview. Storybook and tests use `createInMemoryClient` directly, not this export.
 */
export const tesseraClient: TesseraClient = demoMode
  ? createInMemoryClient()
  : createHttpClient({ baseUrl: apiUrl ?? '', getAuthHeader: authHeader })
