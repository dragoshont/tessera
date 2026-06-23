import { formatDistanceToNowStrict } from 'date-fns'
import type { Connection } from '../data/types'

/** Relative phrasing like "12 days ago"; em dash when we genuinely don't know. */
export function relativeTime(value?: string): string {
  if (!value) return '—'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return '—'
  return formatDistanceToNowStrict(date, { addSuffix: true })
}

/**
 * Honest expiry rendering (spec C.1 "Expires honesty rule"): cookies often have
 * no readable TTL, so render exactly one of `—`, `in N days` / `expired …`,
 * `~estimated`, or `unknown`. Never fabricate a precise date.
 */
export function formatExpiry(
  connection: Pick<Connection, 'status' | 'expiresAt' | 'expiryIsEstimated'>,
): string {
  if (connection.status === 'absent' || connection.status === 'error') return '—'
  if (connection.expiresAt) {
    const date = new Date(connection.expiresAt)
    if (!Number.isNaN(date.getTime())) {
      return date.getTime() < Date.now()
        ? `expired ${formatDistanceToNowStrict(date, { addSuffix: true })}`
        : `in ${formatDistanceToNowStrict(date)}`
    }
  }
  if (connection.expiryIsEstimated) return '~estimated'
  return 'unknown'
}

/** The "~estimated"/"unknown" values are rendered muted so they read as honest, not authoritative. */
export function isExpiryUncertain(text: string): boolean {
  return text === '~estimated' || text === 'unknown' || text === '—'
}

/** Rows that pull the operator's eye: expiring soon, error, or blocked on a human. */
export function needsAttention(connection: Pick<Connection, 'status'>): boolean {
  return (
    connection.status === 'expiring_soon' ||
    connection.status === 'error' ||
    connection.status === 'needs_human' ||
    // ADR 0025: an unconfirmed or dead session is degraded — surface it, don't hide it.
    connection.status === 'unverified' ||
    connection.status === 'dead'
  )
}
