import { describe, expect, it } from 'vitest'
import { demoLiveViewHandle } from '../../data/fixtures'
import { handoffReducer, initialHandoffState, type HandoffState } from './handoff-machine'

describe('handoffReducer', () => {
  it('tessera-session-done drives WaitingForYou → Verifying → Done', () => {
    // `tessera-session-done` (or the manual "I'm done" fallback) maps to SESSION_DONE;
    // the broker's read-only self-test then maps to VERIFY_OK.
    let state: HandoffState = initialHandoffState('waiting', demoLiveViewHandle)

    state = handoffReducer(state, { type: 'SESSION_DONE' })
    expect(state.status).toBe('verifying')

    state = handoffReducer(state, { type: 'VERIFY_OK' })
    expect(state.status).toBe('done')
  })

  it('walks the happy path Preflight → Connecting → Waiting', () => {
    let state = initialHandoffState('preflight')

    state = handoffReducer(state, { type: 'START' })
    expect(state.status).toBe('connecting')
    expect(state.handle).toBeNull()

    state = handoffReducer(state, { type: 'CONNECT_SUCCESS', handle: demoLiveViewHandle })
    // Still connecting (skeleton) until the canvas signals ready.
    expect(state.status).toBe('connecting')
    expect(state.handle).toEqual(demoLiveViewHandle)

    state = handoffReducer(state, { type: 'IFRAME_READY' })
    expect(state.status).toBe('waiting')
  })

  it('maps the fail-closed 503 to Unavailable (a calm explainer, not an error)', () => {
    let state = initialHandoffState('connecting')
    state = handoffReducer(state, { type: 'CONNECT_UNAVAILABLE', reason: 'no worker (fail-closed)' })

    expect(state.status).toBe('unavailable')
    expect(state.unavailableReason).toBe('no worker (fail-closed)')
    expect(state.errorReason).toBeNull()
  })

  it('lapses a running window to Expired, then re-arms via RESTART', () => {
    let state = initialHandoffState('waiting', demoLiveViewHandle)

    state = handoffReducer(state, { type: 'EXPIRE' })
    expect(state.status).toBe('expired')

    state = handoffReducer(state, { type: 'RESTART' })
    expect(state.status).toBe('connecting')
    expect(state.handle).toBeNull()
  })

  it('does not expire while Verifying (the countdown is frozen)', () => {
    const state = initialHandoffState('verifying', demoLiveViewHandle)
    expect(handoffReducer(state, { type: 'EXPIRE' }).status).toBe('verifying')
  })

  it('bounces through Reconnecting and back to Waiting', () => {
    let state = initialHandoffState('waiting', demoLiveViewHandle)

    state = handoffReducer(state, { type: 'DISCONNECTED' })
    expect(state.status).toBe('reconnecting')

    state = handoffReducer(state, { type: 'RECONNECTED' })
    expect(state.status).toBe('waiting')
  })

  it('a verify failure surfaces as Error with a one-line reason', () => {
    let state = initialHandoffState('verifying', demoLiveViewHandle)
    state = handoffReducer(state, { type: 'VERIFY_FAIL', reason: 'session rejected' })

    expect(state.status).toBe('error')
    expect(state.errorReason).toBe('session rejected')
  })
})
