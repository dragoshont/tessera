import type {
  AuditFeed,
  AuditRow,
  Connection,
  CreateConnectionInput,
  Delegation,
  FollowUpAcceptInput,
  FollowUpDetail,
  FollowUpFieldDecisionInput,
  FollowUpImportInput,
  FollowUpList,
  FollowUpMutationResult,
  FollowUpWhy,
  LiveViewHandle,
  LiveViewResult,
  Module,
  PendingWrite,
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
  pendingWrites as fixturePendingWrites,
  people as fixturePeople,
  portalConfigDev,
  recipes as fixtureRecipes,
} from '../data/fixtures'
import { authHeader } from '../app/auth'
import { getApiOrigin } from '../app/runtime'

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
   * Writes the broker is holding for out-of-band human approval (`GET /portal/pending-writes`,
   * ADR 0023) — self-scoped to the signed-in person server-side. Secret-free: a human
   * summary + a short body excerpt, never the payload or a credential.
   */
  getPendingWrites(): Promise<PendingWrite[]>
  /**
   * Approve a held write (`POST /portal/pending-writes/{id}/approve`) → authorizes the
   * original caller to re-issue the *exact* request. Does NOT perform the write itself;
   * returns the updated record. Throws `HttpError(404)` if it is not held for the caller.
   */
  approvePendingWrite(id: string): Promise<PendingWrite>
  /** Deny a held write (`POST /portal/pending-writes/{id}/deny`) → it will never be forwarded. */
  denyPendingWrite(id: string): Promise<PendingWrite>
  /**
   * Who/what may act as a person (`GET /portal/delegations`). Self by default; an
   * operator may pass a principal, or omit it for every grant (incl. automation).
   */
  listDelegations(principal?: string): Promise<Delegation[]>
  /** The loaded connector catalog + egress posture (`GET /portal/modules`). */
  listModules(): Promise<Module[]>
  /** One connection's rotation schedule (`GET /portal/connections/{id}/schedule`). */
  getSchedule(connectionId: string): Promise<Schedule>
  listFollowUps(view: 'attention' | 'tracked'): Promise<FollowUpList>
  getFollowUp(followUpId: string): Promise<FollowUpDetail | undefined>
  getFollowUpWhy(followUpId: string): Promise<FollowUpWhy | undefined>
  importFollowUpFixture(fixtureId: string, input: FollowUpImportInput): Promise<FollowUpMutationResult>
  acceptFollowUp(followUpId: string, input: FollowUpAcceptInput): Promise<FollowUpMutationResult>
  correctFollowUp(followUpId: string, input: FollowUpFieldDecisionInput): Promise<FollowUpMutationResult>
  resolveFollowUp(followUpId: string, input: FollowUpFieldDecisionInput): Promise<FollowUpMutationResult>
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
  /** Held writes awaiting out-of-band approval (ADR 0023); approve/deny mutate this set. */
  pendingWrites?: PendingWrite[]
  /** Per-connection schedule override; defaults to a synthesized "none" schedule. */
  schedules?: Record<string, Schedule>
  followUps?: FollowUpDetail[]
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
  // A mutable copy so approve/deny is reflected by later getPendingWrites reads.
  const pendingWrites = [...(seed.pendingWrites ?? fixturePendingWrites)]
  const followUps = [...(seed.followUps ?? [])]

  function requireFollowUp(followUpId: string, expectedVersion?: number): FollowUpDetail {
    const followUp = followUps.find((item) => item.followUpId === followUpId)
    if (!followUp) throw new HttpError(404, 'Follow-up not found.')
    if (expectedVersion !== undefined && followUp.version !== expectedVersion) {
      throw new HttpError(409, 'Follow-up version is stale.')
    }
    return followUp
  }

  function saveFollowUp(next: FollowUpDetail): FollowUpMutationResult {
    const index = followUps.findIndex((item) => item.followUpId === next.followUpId)
    if (index >= 0) followUps[index] = next
    else followUps.push(next)
    return { followUpId: next.followUpId, version: next.version, replayed: false }
  }

  function demoRevision(
    revisionId: string,
    field: FollowUpDetail['revisions'][number]['field'],
    value: string,
    state: FollowUpDetail['revisions'][number]['state'],
    evidenceRef: string,
    sourceTimestamp: string,
    lineageRevisionRefs: string[] = [],
    correctionEvidenceRef: string | null = null,
  ): FollowUpDetail['revisions'][number] {
    return {
      revisionId,
      field,
      value,
      state,
      evidenceRefs: [evidenceRef],
      sourceTimestamp,
      parserVersion: correctionEvidenceRef ? '1' : 'followup.fixture.v1',
      confidence: correctionEvidenceRef ? 1 : 0.95,
      correctionEvidenceRef,
      lineageRevisionRefs,
      createdAt: sourceTimestamp,
    }
  }

  function timelineEntry(
    detail: FollowUpDetail | undefined,
    kind: string,
    summary: string,
    evidenceRef: string,
    sourceTimestamp: string,
    field: FollowUpDetail['timeline'][number]['field'] = null,
  ): FollowUpDetail['timeline'][number] {
    return {
      sequence: (detail?.timeline.at(-1)?.sequence ?? 0) + 1,
      kind,
      field,
      summary,
      evidenceRef,
      sourceTimestamp,
      recordedAt: sourceTimestamp,
    }
  }

  // Mirrors the broker: only the bound principal may decide their own *held* write;
  // a wrong owner, an already-decided write, or an unknown id is a 404 (not found).
  // Swaps in a fresh record rather than mutating in place, so the shared fixture
  // objects stay pristine across client instances (tests + Storybook).
  function decidePendingWrite(id: string, status: 'approved' | 'denied'): PendingWrite {
    const index = pendingWrites.findIndex((entry) => entry.id === id)
    const write = index >= 0 ? pendingWrites[index] : undefined
    if (!write || write.principal !== currentPrincipal || write.status !== 'pending') {
      throw new HttpError(404, 'pending write is not held for you')
    }
    const decided: PendingWrite = {
      ...write,
      status,
      decidedBy: currentPrincipal,
      decidedAt: new Date().toISOString(),
    }
    pendingWrites[index] = decided
    return decided
  }

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
    async createConnection({ provider, principal }) {
      // The broker would return the full projection; here we synthesize an honest
      // one: a fresh binding is Absent (no session seeded yet). The server owns the
      // opaque credential reference; the client never selects or receives it.
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
    async getPendingWrites() {
      // Self-scoped + only those still waiting (mirrors the broker's held set).
      return pendingWrites.filter(
        (write) => write.status === 'pending' && write.principal === currentPrincipal,
      )
    },
    async approvePendingWrite(id) {
      return decidePendingWrite(id, 'approved')
    },
    async denyPendingWrite(id) {
      return decidePendingWrite(id, 'denied')
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
    async listFollowUps(view) {
      const items = followUps
        .filter((item) => view === 'attention'
          ? item.status === 'attention' || item.status === 'conflict'
          : item.status === 'tracked' || item.status === 'completed')
        .map((item) => ({
          followUpId: item.followUpId,
          status: item.status,
          version: item.version,
          deliverable: item.revisions.find((revision) => revision.field === 'deliverable' && revision.state === 'current')?.value ?? null,
          counterparty: item.revisions.find((revision) => revision.field === 'counterparty' && revision.state === 'current')?.value ?? null,
          dueAt: item.revisions.find((revision) => revision.field === 'dueAt' && revision.state === 'current')?.value ?? null,
          candidateCount: item.revisions.filter((revision) => revision.state === 'candidate').length,
          conflictCount: item.revisions.filter((revision) => revision.state === 'conflicted').length,
          updatedAt: item.updatedAt,
        }))
      return { items, truncated: false }
    },
    async getFollowUp(followUpId) {
      return followUps.find((item) => item.followUpId === followUpId)
    },
    async getFollowUpWhy(followUpId) {
      const item = followUps.find((followUp) => followUp.followUpId === followUpId)
      if (!item) return undefined
      const fields = item.revisions.reduce<FollowUpWhy['fields']>((grouped, revision) => {
        const revisions = grouped[revision.field] ?? []
        grouped[revision.field] = [...revisions, revision]
        return grouped
      }, {})
      return {
        followUpId,
        fields,
        truncated: false,
      }
    },
    async importFollowUpFixture(fixtureId, input) {
      if (fixtureId === 'initial') {
        const existing = followUps.find((item) => item.followUpId === 'followup:r1-lease-rowan')
        if (existing) return { followUpId: existing.followUpId, version: existing.version, replayed: true }
        const at = '2026-08-10T09:01:00Z'
        return saveFollowUp({
          followUpId: 'followup:r1-lease-rowan',
          status: 'attention',
          version: 1,
          createdAt: at,
          updatedAt: at,
          timelineTruncated: false,
          revisions: [
            demoRevision('revision:r1-initial:deliverable', 'deliverable', 'lease checklist', 'candidate', 'evidence:local.fixture:r1-initial', '2026-08-10T09:00:00Z'),
            demoRevision('revision:r1-initial:counterparty', 'counterparty', 'Rowan', 'candidate', 'evidence:local.fixture:r1-initial', '2026-08-10T09:00:00Z'),
            demoRevision('revision:r1-initial:dueAt', 'dueAt', '2026-08-14', 'candidate', 'evidence:local.fixture:r1-initial', '2026-08-10T09:00:00Z'),
          ],
          timeline: [timelineEntry(undefined, 'Imported', 'Observed initial follow-up evidence.', 'evidence:local.fixture:r1-initial', '2026-08-10T09:00:00Z')],
        })
      }

      const current = requireFollowUp(input.followUpId ?? '', input.expectedVersion)
      const currentField = (field: FollowUpDetail['revisions'][number]['field']) =>
        current.revisions.find((revision) => revision.field === field && revision.state === 'current')
      let revisions = [...current.revisions]
      let status: FollowUpDetail['status'] = 'attention'
      let kind = 'Imported'
      let summary: string
      let evidenceRef: string
      let sourceTimestamp: string
      if (fixtureId === 'monday') {
        const context = ['deliverable', 'counterparty', 'dueAt']
          .map((field) => currentField(field as FollowUpDetail['revisions'][number]['field']))
        if (context.some((revision) => !revision)) throw new HttpError(422, 'Accepted context is required.')
        evidenceRef = 'evidence:local.fixture:r1-monday'
        sourceTimestamp = '2026-08-11T09:00:00Z'
        revisions.push(demoRevision(
          'revision:r1-monday:dueAt',
          'dueAt',
          '2026-08-17',
          'candidate',
          evidenceRef,
          sourceTimestamp,
          context.map((revision) => revision!.revisionId),
        ))
        summary = 'Observed a schedule update resolved from accepted context.'
      } else if (fixtureId === 'conflicting-friday') {
        const dueAt = currentField('dueAt')
        if (!dueAt) throw new HttpError(422, 'Accepted due date context is required.')
        evidenceRef = 'evidence:local.fixture:r1-conflicting-friday'
        sourceTimestamp = '2026-08-18T09:00:00Z'
        revisions = revisions.map((revision) => revision.revisionId === dueAt.revisionId
          ? { ...revision, state: 'conflicted' as const }
          : revision)
        revisions.push(demoRevision(
          'revision:r1-conflicting-friday:dueAt',
          'dueAt',
          '2026-08-14',
          'conflicted',
          evidenceRef,
          sourceTimestamp,
          [dueAt.revisionId],
        ))
        status = 'conflict'
        kind = 'ConflictDetected'
        summary = 'Detected incompatible due-date evidence.'
      } else if (fixtureId === 'sent') {
        const context = ['deliverable', 'counterparty'].map((field) =>
          currentField(field as FollowUpDetail['revisions'][number]['field']))
        if (context.some((revision) => !revision)) throw new HttpError(422, 'Accepted context is required.')
        evidenceRef = 'evidence:local.fixture:r1-sent'
        sourceTimestamp = '2026-08-19T09:00:00Z'
        revisions.push(demoRevision(
          'revision:r1-sent:completedAt',
          'completedAt',
          sourceTimestamp,
          'candidate',
          evidenceRef,
          sourceTimestamp,
          context.map((revision) => revision!.revisionId),
        ))
        summary = 'Observed completion resolved from accepted context.'
      } else {
        throw new HttpError(400, 'Unsupported local continuity fixture.')
      }

      return saveFollowUp({
        ...current,
        status,
        version: current.version + 1,
        updatedAt: sourceTimestamp,
        revisions,
        timeline: [...current.timeline, timelineEntry(current, kind, summary, evidenceRef, sourceTimestamp)],
      })
    },
    async acceptFollowUp(followUpId, input) {
      const current = requireFollowUp(followUpId, input.expectedVersion)
      const selected = new Set(input.candidateRevisionIds ?? current.revisions
        .filter((revision) => revision.state === 'candidate')
        .map((revision) => revision.revisionId))
      if (selected.size === 0) throw new HttpError(409, 'No candidate revision is available.')
      const candidateFields = new Set(current.revisions
        .filter((revision) => selected.has(revision.revisionId))
        .map((revision) => revision.field))
      const evidenceRef = `evidence:user.acceptance:${input.operationId}`
      const revisions = current.revisions.map((revision) => {
        if (selected.has(revision.revisionId)) {
          return { ...revision, state: 'current' as const, evidenceRefs: [...revision.evidenceRefs, evidenceRef] }
        }
        return revision.state === 'current' && candidateFields.has(revision.field)
          ? { ...revision, state: 'superseded' as const }
          : revision
      })
      const completed = revisions.some((revision) => revision.field === 'completedAt' && revision.state === 'current')
      const at = new Date().toISOString()
      return saveFollowUp({
        ...current,
        status: completed ? 'completed' : 'tracked',
        version: current.version + 1,
        updatedAt: at,
        revisions,
        timeline: [...current.timeline, timelineEntry(current, completed ? 'Completed' : 'Accepted', completed ? 'Accepted completion evidence.' : 'Accepted candidate state.', evidenceRef, at)],
      })
    },
    async correctFollowUp(followUpId, input) {
      const current = requireFollowUp(followUpId, input.expectedVersion)
      const prior = current.revisions.find((revision) => revision.field === input.field && revision.state === 'current')
      if (!prior) throw new HttpError(409, 'Only a current field can be corrected.')
      const at = new Date().toISOString()
      const evidenceRef = `evidence:user.correction:${input.operationId}`
      const revisions = current.revisions
        .map((revision) => revision.revisionId === prior.revisionId
          ? { ...revision, state: 'superseded' as const }
          : revision)
      revisions.push(demoRevision(
        `revision:${input.operationId}:${input.field}`,
        input.field,
        input.value,
        'current',
        evidenceRef,
        at,
        [prior.revisionId],
        evidenceRef,
      ))
      return saveFollowUp({
        ...current,
        version: current.version + 1,
        updatedAt: at,
        revisions,
        timeline: [...current.timeline, timelineEntry(current, 'Corrected', `Corrected ${input.field}.`, evidenceRef, at, input.field)],
      })
    },
    async resolveFollowUp(followUpId, input) {
      const current = requireFollowUp(followUpId, input.expectedVersion)
      const conflicts = current.revisions.filter((revision) =>
        revision.field === input.field && revision.state === 'conflicted')
      if (conflicts.length < 2) throw new HttpError(409, 'An explicit conflict is required.')
      const at = new Date().toISOString()
      const evidenceRef = `evidence:user.resolution:${input.operationId}`
      const conflictIds = new Set(conflicts.map((revision) => revision.revisionId))
      const revisions = current.revisions.map((revision) => conflictIds.has(revision.revisionId)
        ? { ...revision, state: 'superseded' as const }
        : revision)
      revisions.push(demoRevision(
        `revision:${input.operationId}:${input.field}`,
        input.field,
        input.value,
        'current',
        evidenceRef,
        at,
        [...conflictIds],
        evidenceRef,
      ))
      return saveFollowUp({
        ...current,
        status: 'tracked',
        version: current.version + 1,
        updatedAt: at,
        revisions,
        timeline: [...current.timeline, timelineEntry(current, 'ConflictResolved', `Resolved ${input.field} conflict.`, evidenceRef, at, input.field)],
      })
    },
  }
}

/** A non-2xx response from the broker (carries the status so callers can branch). */
export class HttpError extends Error {
  readonly status: number
  readonly code?: string

  constructor(status: number, message?: string, code?: string) {
    super(message && message.length > 0 ? message : `HTTP ${status}`)
    this.name = 'HttpError'
    this.status = status
    this.code = code
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

async function responseError(response: Response): Promise<HttpError> {
  const text = await safeText(response)
  try {
    const body = JSON.parse(text) as { code?: string; message?: string; error?: string }
    return new HttpError(response.status, body.message ?? body.error ?? text, body.code)
  } catch {
    return new HttpError(response.status, text)
  }
}

export interface HttpClientOptions {
  baseUrl: string | (() => string)
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
  const base = (): string =>
    (typeof baseUrl === 'function' ? baseUrl() : baseUrl).replace(/\/+$/, '')
  const authHeaders = (): Record<string, string> => getAuthHeader?.() ?? {}

  async function getJson<T>(path: string): Promise<T> {
    const response = await fetch(`${base()}${path}`, {
      method: 'GET',
      headers: { Accept: 'application/json', ...authHeaders() },
    })
    if (!response.ok) throw await responseError(response)
    return (await response.json()) as T
  }

  async function postJson<T>(path: string, body: unknown): Promise<T> {
    const response = await fetch(`${base()}${path}`, {
      method: 'POST',
      headers: { Accept: 'application/json', 'Content-Type': 'application/json', ...authHeaders() },
      body: JSON.stringify(body),
    })
    if (!response.ok) throw await responseError(response)
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
        `${base()}/portal/connections/${encodeURIComponent(connectionId)}/live-view`,
        { method: 'POST', headers: { Accept: 'application/json', ...authHeaders() } },
      )
      // 503 is the fail-closed default (no worker wired) — a calm Unavailable, not
      // an error. Surface the broker's secret-free reason verbatim.
      if (response.status === 503) {
        const body = await safeJson<{ error?: string }>(response)
        return { unavailable: body?.error ?? FAIL_CLOSED_REASON }
      }
      if (!response.ok) throw await responseError(response)
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
    async getPendingWrites() {
      return getJson<PendingWrite[]>('/portal/pending-writes')
    },
    async approvePendingWrite(id) {
      return postJson<PendingWrite>(`/portal/pending-writes/${encodeURIComponent(id)}/approve`, {})
    },
    async denyPendingWrite(id) {
      return postJson<PendingWrite>(`/portal/pending-writes/${encodeURIComponent(id)}/deny`, {})
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
    async listFollowUps(view) {
      return getJson<FollowUpList>(`/portal/continuity/follow-ups?view=${encodeURIComponent(view)}`)
    },
    async getFollowUp(followUpId) {
      try {
        return await getJson<FollowUpDetail>(
          `/portal/continuity/follow-ups/${encodeURIComponent(followUpId)}`,
        )
      } catch (error) {
        if (error instanceof HttpError && error.status === 404) return undefined
        throw error
      }
    },
    async getFollowUpWhy(followUpId) {
      try {
        return await getJson<FollowUpWhy>(
          `/portal/continuity/follow-ups/${encodeURIComponent(followUpId)}/why`,
        )
      } catch (error) {
        if (error instanceof HttpError && error.status === 404) return undefined
        throw error
      }
    },
    async importFollowUpFixture(fixtureId, input) {
      return postJson<FollowUpMutationResult>(
        `/portal/continuity/fixtures/${encodeURIComponent(fixtureId)}/import`,
        input,
      )
    },
    async acceptFollowUp(followUpId, input) {
      return postJson<FollowUpMutationResult>(
        `/portal/continuity/follow-ups/${encodeURIComponent(followUpId)}/accept`,
        input,
      )
    },
    async correctFollowUp(followUpId, input) {
      return postJson<FollowUpMutationResult>(
        `/portal/continuity/follow-ups/${encodeURIComponent(followUpId)}/correct`,
        input,
      )
    },
    async resolveFollowUp(followUpId, input) {
      return postJson<FollowUpMutationResult>(
        `/portal/continuity/follow-ups/${encodeURIComponent(followUpId)}/resolve`,
        input,
      )
    },
  }
}

const apiUrl = import.meta.env.VITE_TESSERA_API_URL
const automatedE2e = import.meta.env.MODE === 'e2e' && navigator.webdriver

/**
 * The app's client. By default it talks to the REAL .NET broker over HTTP at the
 * same origin that serves the SPA (`baseUrl: ''`) — `VITE_TESSERA_API_URL` is an
 * optional override for `npm run dev` against a separately-running broker (e.g.
 * http://127.0.0.1:8080). Per-request auth flows through `authHeader()` (the dev
 * principal header or a verified `Authorization: Bearer …`), never a baked-in token.
 *
 * Storybook and tests use `createInMemoryClient` directly, never this production export.
 */
export const tesseraClient: TesseraClient = automatedE2e
  ? createInMemoryClient()
  : createHttpClient({ baseUrl: () => apiUrl ?? getApiOrigin(), getAuthHeader: authHeader })
