import { addDays, subDays, subHours } from 'date-fns'
import type { Connection, LiveViewHandle, Person, PortalConfig, Recipe } from './types'

// Generic identities only — never real names (spec first principles).
// Timestamps are anchored to "now" so relative phrasing ("12 days ago") stays
// truthful whenever the fixtures are rendered.
const now = new Date()
const iso = (date: Date) => date.toISOString()

export const currentUserPrincipal = 'alice@example.com'

/**
 * Alice (the operator) owns one connection in each of the four health states so
 * the My-accounts table can show Live · Expiring soon · Absent · Error at once.
 * Bob owns a single live connection; Carol owns none.
 */
export const connections: Connection[] = [
  {
    connectionId: 'conn-alice-health',
    ownerPrincipal: 'alice@example.com',
    provider: 'Health Portal',
    displayName: 'Health Portal',
    status: 'live',
    lastSeededAt: iso(subDays(now, 12)),
    lastUsedAt: iso(subHours(now, 3)),
    expiresAt: undefined,
    expiryIsEstimated: true,
    hasCookies: true,
    hasRefreshToken: true,
    hasAccessToken: false,
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
  },
  {
    connectionId: 'conn-bob-health',
    ownerPrincipal: 'bob@example.com',
    provider: 'Health Portal',
    displayName: 'Health Portal',
    status: 'live',
    lastSeededAt: iso(subDays(now, 2)),
    lastUsedAt: iso(subHours(now, 20)),
    expiresAt: undefined,
    expiryIsEstimated: true,
    hasCookies: true,
    hasRefreshToken: true,
    hasAccessToken: false,
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
