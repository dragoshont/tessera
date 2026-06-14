import { useCallback, useEffect, useReducer, useRef } from 'react'
import type { LiveViewResult } from '../../data/types'
import type { LiveViewMessage } from './LiveViewIframe'
import {
  initialHandoffState,
  handoffReducer,
  type HandoffState,
  type HandoffStatus,
} from './handoff-machine'

export type VerifyResult = { ok: true } | { ok: false; reason: string }

export interface UseHandoffOptions {
  connectionId: string
  /** Mint a handle for the connection (the broker client, or a demo seed). */
  requestLiveView: (connectionId: string) => Promise<LiveViewResult>
  /** Read-only self-test; defaults to a brief success (the real probe is a backend
   *  gap — never claim connected without it once wired). */
  verify?: () => Promise<VerifyResult>
  /** Start on a specific state (stories/tests). */
  initialStatus?: HandoffStatus
  /** Skip the pre-flight dialog and mint immediately (demo/stories only). */
  autoStart?: boolean
}

export interface UseHandoffResult {
  state: HandoffState
  /** Pre-flight [Start] → mint a handle. */
  start: () => void
  /** Re-arm from Expired / Error / Paused → mint a fresh handle. */
  restart: () => void
  /** Manual "I'm done" fallback. */
  markDone: () => void
  /** Park as an Action-required item (leave-and-resume). */
  pause: () => void
  /** The iframe became ready (load or `tessera-session-ready`). */
  onIframeReady: () => void
  /** The visible countdown reached 0. */
  onExpire: () => void
  /** Route a worker postMessage event into the machine. */
  onSessionEvent: (message: LiveViewMessage) => void
}

const DEFAULT_VERIFY_MS = 1200

function defaultVerify(): Promise<VerifyResult> {
  // A brief, honest pause standing in for the broker's read-only self-test.
  return new Promise((resolve) => setTimeout(() => resolve({ ok: true }), DEFAULT_VERIFY_MS))
}

/**
 * Orchestrates the Live hand-off: owns the status machine and drives it from the
 * broker client (mint → handle / fail-closed), the iframe channel, the countdown,
 * and the verify probe. Critically, no work happens on mount unless `autoStart`
 * is set — the pre-flight dialog gates the first broker call (anti "refresh on
 * page load").
 */
export function useHandoff({
  connectionId,
  requestLiveView,
  verify,
  initialStatus,
  autoStart = false,
}: UseHandoffOptions): UseHandoffResult {
  const [state, dispatch] = useReducer(handoffReducer, initialHandoffState(initialStatus))

  // Guards so a mint or verify can't double-fire (React Strict Mode / re-renders).
  const mintingRef = useRef(false)
  const verifyingRef = useRef(false)
  const autoStartedRef = useRef(false)

  const mint = useCallback(async () => {
    if (mintingRef.current) return
    mintingRef.current = true
    try {
      const result = await requestLiveView(connectionId)
      if ('handle' in result) {
        dispatch({ type: 'CONNECT_SUCCESS', handle: result.handle })
      } else {
        dispatch({ type: 'CONNECT_UNAVAILABLE', reason: result.unavailable })
      }
    } catch (error) {
      dispatch({
        type: 'CONNECT_ERROR',
        reason: error instanceof Error ? error.message : 'Could not start the hand-off.',
      })
    } finally {
      mintingRef.current = false
    }
  }, [connectionId, requestLiveView])

  const start = useCallback(() => {
    dispatch({ type: 'START' })
    void mint()
  }, [mint])

  const restart = useCallback(() => {
    dispatch({ type: 'RESTART' })
    void mint()
  }, [mint])

  const markDone = useCallback(() => dispatch({ type: 'SESSION_DONE' }), [])
  const pause = useCallback(() => dispatch({ type: 'PAUSE' }), [])
  const onIframeReady = useCallback(() => dispatch({ type: 'IFRAME_READY' }), [])
  const onExpire = useCallback(() => dispatch({ type: 'EXPIRE' }), [])

  const onSessionEvent = useCallback((message: LiveViewMessage) => {
    switch (message) {
      case 'tessera-session-ready':
        dispatch({ type: 'IFRAME_READY' })
        break
      case 'tessera-session-done':
        dispatch({ type: 'SESSION_DONE' })
        break
      case 'tessera-session-disconnected':
        dispatch({ type: 'DISCONNECTED' })
        break
      case 'tessera-session-expired':
        dispatch({ type: 'EXPIRE' })
        break
      case 'tessera-session-error':
        dispatch({ type: 'SESSION_ERROR', reason: 'The remote browser reported an error.' })
        break
    }
  }, [])

  // Demo/story auto-start: drive straight from pre-flight on mount, once.
  useEffect(() => {
    if (autoStart && !autoStartedRef.current && state.status === 'preflight') {
      autoStartedRef.current = true
      start()
    }
  }, [autoStart, start, state.status])

  // Run the verify probe whenever we enter Verifying (server-detected success or
  // the manual fallback both land here). Guarded to run once per entry.
  useEffect(() => {
    if (state.status !== 'verifying') {
      verifyingRef.current = false
      return
    }
    if (verifyingRef.current) return
    verifyingRef.current = true

    let cancelled = false
    const run = verify ?? defaultVerify
    void run().then((result) => {
      if (cancelled) return
      if (result.ok) dispatch({ type: 'VERIFY_OK' })
      else dispatch({ type: 'VERIFY_FAIL', reason: result.reason })
    })
    return () => {
      cancelled = true
    }
  }, [state.status, verify])

  return { state, start, restart, markDone, pause, onIframeReady, onExpire, onSessionEvent }
}
