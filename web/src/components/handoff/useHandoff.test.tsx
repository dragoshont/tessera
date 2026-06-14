import { describe, expect, it, vi } from 'vitest'
import { act, renderHook, waitFor } from '@testing-library/react'
import { useHandoff } from './useHandoff'
import { demoLiveViewHandle } from '../../data/fixtures'

describe('useHandoff — orchestration', () => {
  it('does no work until start() — the pre-flight gates the first broker call', () => {
    const requestLiveView = vi.fn(async () => ({ handle: demoLiveViewHandle }))
    const { result } = renderHook(() => useHandoff({ connectionId: 'x', requestLiveView }))

    expect(result.current.state.status).toBe('preflight')
    expect(requestLiveView).not.toHaveBeenCalled()
  })

  it('drives the full happy path: start → connecting → waiting → verifying → done', async () => {
    const requestLiveView = vi.fn(async () => ({ handle: demoLiveViewHandle }))
    const verify = vi.fn(async () => ({ ok: true as const }))
    const { result } = renderHook(() => useHandoff({ connectionId: 'x', requestLiveView, verify }))

    act(() => {
      result.current.start()
    })
    // The handle is minted asynchronously; status holds at connecting until ready.
    await waitFor(() => expect(result.current.state.handle).not.toBeNull())
    expect(result.current.state.status).toBe('connecting')
    expect(requestLiveView).toHaveBeenCalledWith('x')

    act(() => {
      result.current.onIframeReady()
    })
    expect(result.current.state.status).toBe('waiting')

    // The manual "I'm done" fallback drives Verifying just like tessera-session-done.
    act(() => {
      result.current.markDone()
    })
    expect(result.current.state.status).toBe('verifying')

    await waitFor(() => expect(result.current.state.status).toBe('done'))
    expect(verify).toHaveBeenCalledTimes(1)
  })

  it('a tessera-session-done message also drives Verifying → Done', async () => {
    const requestLiveView = vi.fn(async () => ({ handle: demoLiveViewHandle }))
    const verify = vi.fn(async () => ({ ok: true as const }))
    const { result } = renderHook(() => useHandoff({ connectionId: 'x', requestLiveView, verify }))

    act(() => {
      result.current.start()
    })
    await waitFor(() => expect(result.current.state.handle).not.toBeNull())
    act(() => {
      result.current.onIframeReady()
    })
    act(() => {
      result.current.onSessionEvent('tessera-session-done')
    })
    expect(result.current.state.status).toBe('verifying')
    await waitFor(() => expect(result.current.state.status).toBe('done'))
  })

  it('fail-closed seed lands on Unavailable (no error spinner)', async () => {
    const requestLiveView = vi.fn(async () => ({ unavailable: 'no worker (fail-closed)' }))
    const { result } = renderHook(() => useHandoff({ connectionId: 'x', requestLiveView }))

    act(() => {
      result.current.start()
    })
    await waitFor(() => expect(result.current.state.status).toBe('unavailable'))
    expect(result.current.state.unavailableReason).toBe('no worker (fail-closed)')
  })

  it('autoStart mints on mount without a pre-flight click', async () => {
    const requestLiveView = vi.fn(async () => ({ handle: demoLiveViewHandle }))
    const { result } = renderHook(() =>
      useHandoff({ connectionId: 'x', requestLiveView, autoStart: true }),
    )

    await waitFor(() => expect(requestLiveView).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(result.current.state.handle).not.toBeNull())
  })
})
