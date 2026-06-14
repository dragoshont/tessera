// Data contract for the Tessera admin portal read-model.
//
// These field names mirror the .NET `connection` projection described in
// docs/specs/admin-portal.md §8 and docs/ui/tessera-admin-portal-ui-spec.md.
// IMPORTANT: there is no secret-value field anywhere in this contract. The
// portal only ever learns *presence* (has cookies / refresh / access token),
// never the value. "Tessera can't show this — that's the point."

export type Role = 'Admin' | 'Member'

/** How the broker expects the operator to sign in (from `GET /portal/config`). */
export type AuthMode = 'dev' | 'oidc' | 'none'

/** OIDC parameters the SPA needs to start an Authorization Code + PKCE redirect.
 *  No secret here — the client is public and the token is fetched at runtime. */
export interface OidcConfig {
  authority: string
  clientId: string
  scope: string
}

/** Sign-in configuration the broker advertises at `GET /portal/config`. The SPA
 *  fetches this first to decide which sign-in surface to render. */
export interface PortalConfig {
  authMode: AuthMode
  /** True when the broker trusts a loopback `X-Tessera-Dev-Principal` header. */
  devLoopback: boolean
  oidc?: OidcConfig
}

/** Why a sign-in attempt didn't land the operator in the portal. */
export type SignInError = 'not-allowed' | 'oidc-failed'

/** A connectable provider offered by the connect wizard (`GET /portal/recipes`). */
export interface Recipe {
  /** The slug the broker keys connections on, e.g. "health". */
  provider: string
  /** The human label shown in the picker, e.g. "Health Portal". */
  displayName: string
}

/**
 * The connect-wizard write (`POST /portal/connections`).
 *
 * IMPORTANT: `credential` is the NAME of the vault secret that holds (or will
 * hold) the session bundle — never a password or secret value. The portal only
 * ever names the store secret; it never carries its contents.
 */
export interface CreateConnectionInput {
  provider: string
  principal: string
  credential: string
}

export type ConnectionStatus =
  | 'live'
  | 'expiring_soon'
  | 'absent'
  | 'error'
  | 'seeding'
  | 'needs_human'

/** A household/team member, derived from OIDC principals + the admins allow-list (no DB — spec §7b). */
export interface Person {
  /** The verified OIDC principal the broker keys grants/bindings on, e.g. "alice@example.com". */
  principal: string
  role: Role
  connectionCount: number
  needsAttentionCount: number
}

/** A connected account: a seeded session the broker can act with, reported status-only. */
export interface Connection {
  connectionId: string
  /** The principal this connection acts *as*. */
  ownerPrincipal: string
  provider: string
  displayName: string
  /** Server-provided, trustworthy favicon. Absent until the backend can guarantee it (labeled gap). */
  faviconUrl?: string
  status: ConnectionStatus
  lastSeededAt?: string
  /** "last successfully used" — shown alongside expiry for trust. */
  lastUsedAt?: string
  expiresAt?: string
  /** True when `expiresAt` is a guess (cookies often have no readable TTL). */
  expiryIsEstimated: boolean
  // Presence flags only — never the value.
  hasCookies: boolean
  hasRefreshToken: boolean
  hasAccessToken: boolean
  /** Whose credential backs this connection (ADR 0020): a household/brokered key, the person's own login, or a guardian-seeded dependent. */
  owner?: CredentialOwner
  /** For an `owner: 'dependent'` connection, the guardian who seeded it. */
  guardian?: string | null
}

/** Whose credential a connection is backed by (ADR 0020). */
export type CredentialOwner = 'service' | 'user' | 'dependent'

/** Whether the embedded worker browser is interactive (login) or view-only. */
export type LiveViewMode = 'readonly' | 'readwrite'

/**
 * A short-TTL, single-use hand-off handle the broker mints for the Live stage.
 *
 * IMPORTANT: `liveViewUrl` is NOT a secret value — no cookie/token is ever exposed
 * to the portal. But it IS short-TTL and single-use, so treat it like a capability:
 * never render it as text, never log it, and let it expire. The portal always wraps
 * it in Tessera chrome (target strip + trust line); the raw worker URL is never shown.
 */
export interface LiveViewHandle {
  /** The worker browser endpoint the iframe points at (never printed as text). */
  liveViewUrl: string
  mode: LiveViewMode
  sessionTtlSeconds: number
  /** ISO instant the handle dies; drives the visible countdown → graceful re-arm. */
  expiresAt: string
  /** Server-verified hostname — the anti-phishing anchor in the target strip. */
  targetHostname: string
  /**
   * Server-provided favicon (labeled backend gap). The UI deliberately does NOT
   * render a remote favicon (anti-spoofing) — it uses a local ProviderIcon tile.
   */
  faviconUrl?: string
}

/** Either a live hand-off handle, or a calm reason it's unavailable (fail-closed 503). */
export type LiveViewResult = { handle: LiveViewHandle } | { unavailable: string }

// ── Awareness dashboard (ADR 0017) ──────────────────────────────────────────
// Read-only, secret-free projections that answer "who/what may act as me, what
// modules are loaded, is a job running, and what happened?" No secret value
// appears in any of these — only ids, enums, counts, and timestamps.

/** The decision outcome of one brokering call. */
export type AuditEffect = 'allow' | 'deny' | 'step-up'

/** One secret-free activity row (`GET /portal/audit`). */
export interface AuditRow {
  timestamp: string
  /** The caller workload (a SPIFFE id / MCP id) — never a human secret. */
  caller: string
  callerVerified: boolean
  /** The person the call acted for, or null for pure automation. */
  onBehalfOf: string | null
  target: string
  action: string
  effect: AuditEffect
  reason: string
  /** The resolved credential *status* (present/absent/…), never a value. */
  credentialStatus: string | null
}

/** The rollup over the scoped activity window. */
export interface AuditSummary {
  total: number
  allow: number
  deny: number
  stepUp: number
  byTarget: Record<string, number>
  byCaller: Record<string, number>
  since: string | null
  until: string | null
}

/** The activity feed: newest-first rows + a window summary (`GET /portal/audit`). */
export interface AuditFeed {
  entries: AuditRow[]
  summary: AuditSummary
}

/** One delegation — who/what may act as a person (`GET /portal/delegations`). */
export interface Delegation {
  /** The caller workload the grant authorizes. */
  caller: string
  target: string
  displayName: string
  actions: string[]
  /** Actions that require a human step-up confirmation before proceeding. */
  stepUpActions: string[]
  /** The distinct action planes (ADR 0019) these actions touch, ordered read → use → manage. */
  planes: ActionPlane[]
  /** True when the grant is pure automation (no delegated human). */
  isAutomation: boolean
  onBehalfOf: string | null
}

/** A loaded connector and what it can do (`GET /portal/modules`). */
export interface Module {
  target: string
  displayName: string
  driver: string
  /** "none" (status-only) or "http" (injectable upstream). */
  egress: 'none' | 'http'
  /** True only when http AND the broker's global egress gate is on. */
  egressEnabled: boolean
  actions: string[]
  /** The distinct action planes (ADR 0019) this module's actions/tools touch, ordered read → use → manage. */
  planes: ActionPlane[]
  toolCount: number
  connectionCount: number
  /** The upstream host (host only, never a path/secret), or null. */
  upstreamHost: string | null
}

/** An action plane (ADR 0019): observe (read), operate (use), or reshape (manage). */
export type ActionPlane = 'read' | 'use' | 'manage'

/** Who owns rotating a connection's session. */
export type RotationOwner = 'none' | 'external' | 'tessera'

/** One connection's rotation schedule (`GET /portal/connections/{id}/schedule`). */
export interface Schedule {
  connectionId: string
  rotationOwner: RotationOwner
  refreshConfigured: boolean
  detail: string
  /** Populated only once Tessera itself owns + tracks rotation (Mode U). */
  lastRotatedAt: string | null
  nextRotationAt: string | null
}
