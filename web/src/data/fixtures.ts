import { addDays, addMinutes, subDays, subHours, subMinutes } from 'date-fns'
import type {
  AuditRow,
  Connection,
  Delegation,
  LiveViewHandle,
  Module,
  PendingWrite,
  Person,
  PortalConfig,
  Recipe,
} from './types'

// Generic identities only — never real names (spec first principles).
// Timestamps are anchored to "now" so relative phrasing ("12 days ago") stays
// truthful whenever the fixtures are rendered.
const now = new Date()
const iso = (date: Date) => date.toISOString()

export const currentUserPrincipal = 'alice@example.com'

/**
 * Alice (the operator) owns one connection in each of four health states so the
 * My-accounts table can show Unverified · Expiring soon · Absent · Error at once
 * (ADR 0025: a present-but-unconfirmed session is "Unverified", not a false "Live").
 * Bob's Health Portal is the deliberate verified-"Live" sample — it carries a
 * lastVerifiedAt, so "live" is earned by a verdict, not assumed from presence.
 * Bob owns a single live connection; Carol owns none.
 */
export const connections: Connection[] = [
  {
    connectionId: 'conn-alice-health',
    ownerPrincipal: 'alice@example.com',
    provider: 'Health Portal',
    displayName: 'Health Portal',
    // ADR 0025: present, but Tessera has not independently confirmed it is alive
    // (no rotation verdict yet) -> honest "unverified", never an optimistic "live".
    status: 'unverified',
    lastSeededAt: iso(subDays(now, 12)),
    lastUsedAt: iso(subHours(now, 3)),
    expiresAt: undefined,
    expiryIsEstimated: true,
    hasCookies: true,
    hasRefreshToken: true,
    hasAccessToken: false,
    owner: 'user',
  },
  {
    connectionId: 'conn-alice-utility',
    ownerPrincipal: 'alice@example.com',
    provider: 'Utility Co',
    displayName: 'Utility Co',
    status: 'expiring_soon',
    lastSeededAt: iso(subDays(now, 88)),
    lastUsedAt: iso(subDays(now, 1)),
    expiresAt: iso(addDays(now, 2)),
    expiryIsEstimated: false,
    hasCookies: true,
    hasRefreshToken: true,
    hasAccessToken: true,
    owner: 'service',
  },
  {
    connectionId: 'conn-alice-marketplace',
    ownerPrincipal: 'alice@example.com',
    provider: 'Marketplace',
    displayName: 'Marketplace',
    status: 'absent',
    lastSeededAt: undefined,
    lastUsedAt: undefined,
    expiresAt: undefined,
    expiryIsEstimated: false,
    hasCookies: false,
    hasRefreshToken: false,
    hasAccessToken: false,
    owner: 'user',
  },
  {
    connectionId: 'conn-alice-webmail',
    ownerPrincipal: 'alice@example.com',
    provider: 'Webmail',
    displayName: 'Webmail',
    status: 'error',
    lastSeededAt: iso(subDays(now, 3)),
    lastUsedAt: iso(subDays(now, 3)),
    expiresAt: undefined,
    expiryIsEstimated: false,
    hasCookies: true,
    hasRefreshToken: false,
    hasAccessToken: false,
    owner: 'user',
  },
  {
    connectionId: 'conn-bob-health',
    ownerPrincipal: 'bob@example.com',
    provider: 'Health Portal',
    displayName: 'Health Portal',
    status: 'live',
    lastSeededAt: iso(subDays(now, 2)),
    lastUsedAt: iso(subHours(now, 20)),
    // "live" is earned: Tessera confirmed the session alive 1h ago (ADR 0025).
    lastVerifiedAt: iso(subHours(now, 1)),
    expiresAt: undefined,
    expiryIsEstimated: true,
    hasCookies: true,
    hasRefreshToken: true,
    hasAccessToken: false,
    owner: 'user',
  },
]

const connectionCountFor = (principal: string) =>
  connections.filter((connection) => connection.ownerPrincipal === principal).length

/**
 * People are a projection (no DB — spec §7b): the OIDC principals already in
 * policy, classified by the admins allow-list. `connectionCount` is derived from
 * the connections above so the two views can never disagree. `needsAttentionCount`
 * is mocked to 0 for the Phase 0 slice.
 */
export const people: Person[] = [
  {
    principal: 'alice@example.com',
    role: 'Admin',
    connectionCount: connectionCountFor('alice@example.com'),
    needsAttentionCount: 0,
  },
  {
    principal: 'bob@example.com',
    role: 'Member',
    connectionCount: connectionCountFor('bob@example.com'),
    needsAttentionCount: 0,
  },
  {
    principal: 'carol@example.com',
    role: 'Member',
    connectionCount: connectionCountFor('carol@example.com'),
    needsAttentionCount: 0,
  },
]

/** Convenience subsets for Storybook states that aren't the default fixture. */
export const aliceConnections = connections.filter(
  (connection) => connection.ownerPrincipal === 'alice@example.com',
)

/**
 * The one verified-"live" connection (bob's Health Portal) — `live` earned by a real
 * `lastVerifiedAt`, not assumed from presence (ADR 0025). The single source of truth
 * for stories/tests that show the confirmed-alive state, so a future fixture change
 * that removes the verified-live sample fails a test instead of silently rendering a
 * blank story.
 */
export const liveConnection: Connection = connections.find(
  (connection) => connection.status === 'live' && Boolean(connection.lastVerifiedAt),
)!

export const allExpiringConnections: Connection[] = aliceConnections.map((connection) => ({
  ...connection,
  status: 'expiring_soon',
  expiresAt: iso(addDays(now, 2)),
  expiryIsEstimated: false,
}))

/**
 * A demo live-view handle for stories, the page's `?demo=1` affordance, and tests.
 * `liveViewUrl` points at a local placeholder canvas (`public/handoff-demo.html`),
 * never a real worker — so the Live stage is fully demoable on fixtures. The real
 * worker `act()` channel is a labeled backend gap (spec R1); without a backend the
 * honest default is the fail-closed Unavailable state.
 */
export const demoLiveViewHandle: LiveViewHandle = {
  liveViewUrl: '/handoff-demo.html',
  mode: 'readwrite',
  sessionTtlSeconds: 300,
  // Anchored ahead of "now" so the countdown reads a healthy ~5:00 when rendered.
  expiresAt: new Date(now.getTime() + 5 * 60_000).toISOString(),
  targetHostname: 'portal.example-health.com',
}

/**
 * The recipes the connect wizard offers (mirrors `GET /portal/recipes`). The
 * `provider` slug is what the broker keys a connection on ("{provider}:{principal}");
 * `displayName` is the human label the picker and `ProviderIcon` render.
 */
export const recipes: Recipe[] = [
  { provider: 'health', displayName: 'Health Portal' },
  { provider: 'utility', displayName: 'Utility Co' },
  { provider: 'marketplace', displayName: 'Marketplace' },
  { provider: 'webmail', displayName: 'Webmail' },
]

/** Sign-in config presets for the three `authMode`s the SignIn surface renders. */
export const portalConfigDev: PortalConfig = { authMode: 'dev', devLoopback: true }
export const portalConfigOidc: PortalConfig = {
  authMode: 'oidc',
  devLoopback: false,
  oidc: {
    authority: 'https://login.microsoftonline.com/common/v2.0',
    clientId: '00000000-0000-0000-0000-000000000000',
    scope: 'openid profile email',
  },
}
export const portalConfigNone: PortalConfig = { authMode: 'none', devLoopback: false }

// ── Awareness dashboard fixtures (ADR 0017) ─────────────────────────────────
// Secret-free projections for the Activity & Access (self) and Observability
// (admin) surfaces. The in-memory client scopes + summarizes these so stories
// render real behaviour (self-scope, summary, step-up) without a backend.

const chatCaller = 'spiffe://tessera.local/chat'

/** Brokering decisions across alice + bob, newest-first when sorted. */
export const auditRows: AuditRow[] = [
  {
    timestamp: iso(subHours(now, 1)),
    caller: chatCaller,
    callerVerified: true,
    onBehalfOf: 'alice@example.com',
    target: 'health',
    action: 'read:appointments',
    effect: 'allow',
    reason: 'granted: read:appointments on behalf of alice@example.com',
    credentialStatus: 'Present',
  },
  {
    timestamp: iso(subHours(now, 3)),
    caller: chatCaller,
    callerVerified: true,
    onBehalfOf: 'alice@example.com',
    target: 'utility',
    action: 'write:pay',
    effect: 'step-up',
    reason: 'step-up required: write:pay needs human confirmation',
    credentialStatus: 'Present',
  },
  {
    timestamp: iso(subHours(now, 6)),
    caller: chatCaller,
    callerVerified: true,
    onBehalfOf: 'bob@example.com',
    target: 'health',
    action: 'read:appointments',
    effect: 'allow',
    reason: 'granted: read:appointments on behalf of bob@example.com',
    credentialStatus: 'Present',
  },
  {
    timestamp: iso(subDays(now, 1)),
    caller: chatCaller,
    callerVerified: true,
    onBehalfOf: 'alice@example.com',
    target: 'marketplace',
    action: 'write:order',
    effect: 'deny',
    reason: 'denied: no grant for write:order',
    credentialStatus: null,
  },
]

/** Who/what may act as each person (a projection of grants). */
export const delegations: Delegation[] = [
  {
    caller: chatCaller,
    target: 'health',
    displayName: 'Health Portal',
    actions: ['read:*'],
    stepUpActions: ['use:book', 'pay:*'],
    planes: ['read'],
    isAutomation: false,
    onBehalfOf: 'alice@example.com',
    owner: 'user',
  },
  {
    caller: chatCaller,
    target: 'utility',
    displayName: 'Utility Co',
    actions: ['read:*', 'use:pay'],
    stepUpActions: ['use:pay'],
    planes: ['read', 'use'],
    isAutomation: false,
    onBehalfOf: 'alice@example.com',
    owner: 'service',
  },
  {
    caller: chatCaller,
    target: 'health',
    displayName: 'Health Portal',
    actions: ['read:appointments'],
    stepUpActions: [],
    planes: ['read'],
    isAutomation: false,
    onBehalfOf: 'bob@example.com',
  },
  {
    caller: 'spiffe://tessera.local/scheduler',
    target: 'utility',
    displayName: 'Utility Co',
    actions: ['read:bill'],
    stepUpActions: [],
    planes: ['read'],
    isAutomation: true,
    onBehalfOf: null,
  },
]

/** The loaded connector catalog (a projection of recipes + egress posture). */
export const modules: Module[] = [
  {
    target: 'health',
    displayName: 'Health Portal',
    driver: 'browser',
    egress: 'none',
    egressEnabled: false,
    actions: ['read:selftest'],
    planes: ['read'],
    toolCount: 0,
    connectionCount: 2,
    upstreamHost: null,
  },
  {
    target: 'utility',
    displayName: 'Utility Co',
    driver: 'browser',
    egress: 'http',
    egressEnabled: true,
    actions: ['read:bill', 'use:pay', 'manage:autopay'],
    planes: ['read', 'use', 'manage'],
    toolCount: 3,
    connectionCount: 1,
    upstreamHost: 'api.utility.example.com',
  },
]

/**
 * Calendar writes the broker is holding for Alice's out-of-band approval (ADR 0023).
 * All are `manage:dav` PUTs the apple-mcp `create_event` tool issued — each held with
 * a single-use challenge and a short TTL until Alice approves it in the portal.
 * Secret-free: a human summary + a small VEVENT excerpt, never a credential.
 */
export const pendingWrites: PendingWrite[] = [
  {
    id: 'cw_8f2a1c',
    principal: currentUserPrincipal,
    target: 'apple-calendar',
    action: 'manage:dav',
    method: 'PUT',
    pathAndQuery: '/1984763/calendars/home/8f2a1c0b-dentist.ics',
    upstreamHost: 'caldav.icloud.com',
    summary: 'Create "Dentist" on Tue 24 Jun, 09:30-10:15 in Home',
    bodyExcerpt:
      'BEGIN:VEVENT\nUID:8f2a1c0b-dentist@apple-mcp\nSUMMARY:Dentist\nDTSTART:20260624T093000Z\nDTEND:20260624T101500Z\nEND:VEVENT',
    status: 'pending',
    createdAt: iso(subMinutes(now, 1)),
    expiresAt: iso(addMinutes(now, 4)),
    decidedBy: null,
    decidedAt: null,
  },
  {
    id: 'cw_3b7e90',
    principal: currentUserPrincipal,
    target: 'apple-calendar',
    action: 'manage:dav',
    method: 'PUT',
    pathAndQuery: '/1984763/calendars/family/3b7e90a4-pickup.ics',
    upstreamHost: 'caldav.icloud.com',
    summary: 'Create "School pickup — Lily" on Wed 25 Jun, 15:00-15:30 in Family',
    bodyExcerpt:
      'BEGIN:VEVENT\nUID:3b7e90a4-pickup@apple-mcp\nSUMMARY:School pickup — Lily\nDTSTART:20260625T150000Z\nDTEND:20260625T153000Z\nLOCATION:Oakfield Primary\nEND:VEVENT',
    status: 'pending',
    createdAt: iso(subMinutes(now, 2)),
    expiresAt: iso(addMinutes(now, 3)),
    decidedBy: null,
    decidedAt: null,
  },
  {
    id: 'cw_d41f56',
    principal: currentUserPrincipal,
    target: 'apple-calendar',
    action: 'manage:dav',
    method: 'PUT',
    pathAndQuery: '/1984763/calendars/home/d41f56e2-dinner.ics',
    upstreamHost: 'caldav.icloud.com',
    summary: 'Create "Dinner with Sam" on Fri 27 Jun, 19:00-21:00 in Home',
    bodyExcerpt:
      'BEGIN:VEVENT\nUID:d41f56e2-dinner@apple-mcp\nSUMMARY:Dinner with Sam\nDTSTART:20260627T190000Z\nDTEND:20260627T210000Z\nEND:VEVENT',
    status: 'pending',
    createdAt: iso(subMinutes(now, 5)),
    expiresAt: iso(addMinutes(now, 2)),
    decidedBy: null,
    decidedAt: null,
  },
]
