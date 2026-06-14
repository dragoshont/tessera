// Data contract for the Tessera admin portal read-model.
//
// These field names mirror the .NET `connection` projection described in
// docs/specs/admin-portal.md §8 and docs/ui/tessera-admin-portal-ui-spec.md.
// IMPORTANT: there is no secret-value field anywhere in this contract. The
// portal only ever learns *presence* (has cookies / refresh / access token),
// never the value. "Tessera can't show this — that's the point."

export type Role = 'Admin' | 'Member'

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
}
