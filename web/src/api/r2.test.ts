import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { R2Problem, r2Api, type R2Action } from './r2'

const originalFetch = globalThis.fetch
let fetchMock: ReturnType<typeof vi.fn>

function response(status: number, body?: unknown): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  } as Response
}

beforeEach(() => {
  fetchMock = vi.fn()
  globalThis.fetch = fetchMock as unknown as typeof fetch
  vi.stubGlobal('crypto', { randomUUID: () => 'fixed-id' })
})

afterEach(() => {
  globalThis.fetch = originalFetch
  vi.unstubAllGlobals()
})

describe('R2 product client contract', () => {
  it('requires the canonical Page response for collection reads', async () => {
    const page = { items: [{ id: 'c1', conversationId: 'c1', title: 'Alpha' }], nextCursor: null }
    fetchMock.mockResolvedValueOnce(response(200, page))

    await expect(r2Api.conversations()).resolves.toEqual(page)
    expect(fetchMock).toHaveBeenCalledWith('/api/v1/conversations', expect.objectContaining({ headers: expect.any(Object) }))
  })

  it('surfaces typed Problem details without collapsing the recovery code', async () => {
    fetchMock.mockResolvedValueOnce(response(409, {
      title: 'version_conflict',
      status: 409,
      code: 'version_conflict',
      detail: 'The resource changed.',
    }))

    await expect(r2Api.settings()).rejects.toMatchObject<R2Problem>({
      status: 409,
      code: 'version_conflict',
      message: 'The resource changed.',
    })
  })

  it('approves only the exact durable action version and supplies idempotency', async () => {
    const action = {
      id: 'action-1',
      version: 3,
      state: 'PROPOSED',
    } as R2Action
    fetchMock.mockResolvedValueOnce(response(202, { ...action, version: 7, state: 'EXTERNALLY_CONFIRMED' }))

    await r2Api.approveAction(action)

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('/api/v1/actions/action-1/approve')
    expect(init.method).toBe('POST')
    expect(init.body).toBe(JSON.stringify({ expectedVersion: 3 }))
    expect(init.headers).toMatchObject({ 'Idempotency-Key': 'approval-fixed-id' })
    expect(init.body).not.toContain('payload')
    expect(init.body).not.toContain('account')
    expect(init.body).not.toContain('target')
  })

  it('parses streamed execution events across response chunk boundaries', async () => {
    const encoder = new TextEncoder()
    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(encoder.encode('id: live-1\nevent: text\ndata: {"del'))
        controller.enqueue(encoder.encode('ta":"hel"}\n\nevent: completed\ndata: {"messageId":"m1"}\n\n'))
        controller.close()
      },
    })
    fetchMock.mockResolvedValueOnce({ ok: true, status: 200, body: stream } as Response)
    const events: Array<{ type: string; data: unknown }> = []

    await r2Api.watchExecution('conversation-1', 'execution-1', new AbortController().signal, (event) => events.push(event))

    expect(events).toEqual([
      { type: 'text', data: { delta: 'hel' } },
      { type: 'completed', data: { messageId: 'm1' } },
    ])
  })
})
