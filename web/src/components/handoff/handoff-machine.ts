import { useReducer } from 'react'
import type { LiveViewHandle } from '../../data/types'

// The Live hand-off status machine (spec C.2). Pure reducer so the transitions are
// trivially testable; the orchestration hook (useHandoff) wires it to the broker
// client, the iframe postMessage channel, the countdown, and the verify probe.
//
//   Preflight → Connecting → WaitingForYou → Verifying → Done
//                   │             │
//                   │             ├─ Disconnected → Reconnecting → (back) / Expired
//                   │             └─ countdown 0 / -expired → Expired → (re-arm)
//                   ├─ 503 fail-closed → Unavailable   (calm "not set up yet")
//                   └─ network/other  → Error          (one-line reason)
//
// Server-detected success (`tessera-session-done`) drives Verifying/Done; the
// manual "I'm done" button is a first-class fallback, never required (spec R2).
export type HandoffStatus =
  | 'preflight'
  | 'connecting'
  | 'waiting'
  | 'verifying'
  | 'done'
  | 'reconnecting'
  | 'expired'
  | 'paused'
  | 'unavailable'
  | 'error'

export interface HandoffState {
  status: HandoffStatus
  /** The short-TTL hand-off handle once minted (never rendered as text). */
  handle: LiveViewHandle | null
  /** Calm reason shown in the fail-closed Unavailable panel. */
  unavailableReason: string | null
  /** One-line reason for the Error panel (never a raw worker URL / secret). */
  errorReason: string | null
}

export type HandoffEvent =
  // Pre-flight → Connecting: the user accepted the ~2-minute explainer.
  | { type: 'START' }
  // A handle was minted; the canvas can mount (status stays Connecting until ready).
  | { type: 'CONNECT_SUCCESS'; handle: LiveViewHandle }
  // 503 fail-closed: no worker wired — degrade to the calm Unavailable explainer.
  | { type: 'CONNECT_UNAVAILABLE'; reason: string }
  // Network / unexpected failure while minting the handle.
  | { type: 'CONNECT_ERROR'; reason: string }
  // `tessera-session-ready` (or the iframe load) — Connecting → WaitingForYou.
  | { type: 'IFRAME_READY' }
  // `tessera-session-done` OR the manual "I'm done" fallback — → Verifying.
  | { type: 'SESSION_DONE' }
  // The read-only self-test passed → Done (never claim connected without it).
  | { type: 'VERIFY_OK' }
  // The self-test failed → Error.
  | { type: 'VERIFY_FAIL'; reason: string }
  // `tessera-session-disconnected` — Waiting → Reconnecting (countdown keeps running).
  | { type: 'DISCONNECTED' }
  // Socket back — Reconnecting → Waiting.
  | { type: 'RECONNECTED' }
  // `tessera-session-expired` OR the countdown hit 0 — → Expired (friendly re-arm).
  | { type: 'EXPIRE' }
  // `tessera-session-error` — → Error.
  | { type: 'SESSION_ERROR'; reason: string }
  // Leave-and-resume: park the seed as an Action-required item.
  | { type: 'PAUSE' }
  // Start again / Try again / Resume — re-arm: never a dead end.
  | { type: 'RESTART' }

// States where the canvas is live and a running countdown can lapse to Expired.
// Verifying is deliberately excluded — its countdown is frozen (spec C.2).
const EXPIRABLE: readonly HandoffStatus[] = ['connecting', 'waiting', 'reconnecting']
// States that can be parked or hit a session-level error.
const ACTIVE: readonly HandoffStatus[] = ['connecting', 'waiting', 'verifying', 'reconnecting']

export function initialHandoffState(
  status: HandoffStatus = 'preflight',
  handle: LiveViewHandle | null = null,
): HandoffState {
  return { status, handle, unavailableReason: null, errorReason: null }
}

export function handoffReducer(state: HandoffState, event: HandoffEvent): HandoffState {
  switch (event.type) {
    case 'START':
      if (state.status !== 'preflight') return state
      return { status: 'connecting', handle: null, unavailableReason: null, errorReason: null }

    case 'CONNECT_SUCCESS':
      // Hold at Connecting (skeleton) until the canvas signals ready; store the
      // handle so the iframe can mount and fire IFRAME_READY.
      if (state.status !== 'connecting') return state
      return { ...state, handle: event.handle }

    case 'CONNECT_UNAVAILABLE':
      return { status: 'unavailable', handle: null, unavailableReason: event.reason, errorReason: null }

    case 'CONNECT_ERROR':
      return { status: 'error', handle: null, unavailableReason: null, errorReason: event.reason }

    case 'IFRAME_READY':
      if (state.status === 'connecting' && state.handle) return { ...state, status: 'waiting' }
      return state

    case 'SESSION_DONE':
      if (state.status === 'waiting' || state.status === 'reconnecting') {
        return { ...state, status: 'verifying' }
      }
      return state

    case 'VERIFY_OK':
      if (state.status === 'verifying') return { ...state, status: 'done' }
      return state

    case 'VERIFY_FAIL':
      if (state.status === 'verifying') return { ...state, status: 'error', errorReason: event.reason }
      return state

    case 'DISCONNECTED':
      if (state.status === 'waiting') return { ...state, status: 'reconnecting' }
      return state

    case 'RECONNECTED':
      if (state.status === 'reconnecting') return { ...state, status: 'waiting' }
      return state

    case 'EXPIRE':
      if (EXPIRABLE.includes(state.status)) return { ...state, status: 'expired' }
      return state

    case 'SESSION_ERROR':
      if (ACTIVE.includes(state.status)) return { ...state, status: 'error', errorReason: event.reason }
      return state

    case 'PAUSE':
      if (ACTIVE.includes(state.status)) return { ...state, status: 'paused' }
      return state

    case 'RESTART':
      // Re-arm from a dead end; the orchestrator re-mints a handle on entering
      // Connecting (Stripe refresh_url lesson — expiry is never a terminal error).
      return { status: 'connecting', handle: null, unavailableReason: null, errorReason: null }

    default:
      return state
  }
}

export function useHandoffMachine(initial?: {
  status?: HandoffStatus
  handle?: LiveViewHandle | null
}) {
  return useReducer(handoffReducer, initialHandoffState(initial?.status, initial?.handle ?? null))
}
