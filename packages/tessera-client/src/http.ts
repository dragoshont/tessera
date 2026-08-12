import type { ProblemBody } from './types'
import { type AuthLease, RouteManager } from './routing'

export class TesseraProblem extends Error {
  readonly status: number
  readonly code: string
  readonly problem?: ProblemBody

  constructor(status: number, code: string, problem?: ProblemBody) {
    super(problem?.detail ?? problem?.title ?? code)
    this.name = 'TesseraProblem'
    this.status = status
    this.code = code
    this.problem = problem
  }
}

export type HttpClientOptions = {
  routes?: RouteManager
  getAccessToken?: () => string | undefined | Promise<string | undefined>
  getAuthLease?: () => Promise<AuthLease>
  send?: (path: string, init: RequestInit) => Promise<Response>
  createIdempotencyKey?: (prefix: string) => string
}

export function createHttpClient(options: HttpClientOptions) {
  if (!options.send && !options.routes) throw new Error('http_transport_required')
  const key = options.createIdempotencyKey ?? ((prefix: string) => `${prefix}-${crypto.randomUUID()}`)
  async function request<T>(path: string, init?: RequestInit): Promise<T> {
    const apiPath = `/api/v1${path}`
    let authLease: AuthLease | undefined
    const response = options.send
      ? await options.send(apiPath, init ?? {})
      : await options.routes!.requestAuthenticated(apiPath, init ?? {}, async () => {
          authLease = options.getAuthLease
            ? await options.getAuthLease()
            : { accessToken: options.getAccessToken ? await options.getAccessToken() : undefined, isCurrent: () => true }
          return authLease
        })
    if (!response.ok) {
      const problem = await readBoundedJsonResponse<ProblemBody>(response, 64 * 1024).catch(() => null)
      if (authLease && !authLease.isCurrent()) throw new Error('session_invalidated')
      throw new TesseraProblem(response.status, problem?.code ?? problem?.title ?? `http_${response.status}`, problem ?? undefined)
    }
    if (response.status === 204) return undefined as T
    const result = await readBoundedJsonResponse<T>(response, 2 * 1024 * 1024)
    if (authLease && !authLease.isCurrent()) throw new Error('session_invalidated')
    return result
  }
  function mutate<T>(path: string, method: 'POST' | 'PATCH' | 'PUT' | 'DELETE', body: unknown, prefix?: string) {
    const headers: Record<string, string> = { 'Content-Type': 'application/json' }
    if (prefix) headers['Idempotency-Key'] = key(prefix)
    return request<T>(path, { method, headers, body: JSON.stringify(body) })
  }
  return { request, mutate }
}

export async function readBoundedJsonResponse<T>(response: Response, maximumBytes: number, timeoutMs = 10_000): Promise<T> {
  if (!response.headers || typeof response.headers.get !== 'function') return response.json() as Promise<T>
  const deadline = Date.now() + timeoutMs
  const declaredLength = Number(response.headers.get('Content-Length'))
  if (Number.isFinite(declaredLength) && declaredLength > maximumBytes) throw new Error('response_too_large')
  if (!response.body) {
    if (typeof response.text !== 'function') return response.json() as Promise<T>
    const text = await withDeadline(response.text(), deadline)
    if (new TextEncoder().encode(text).byteLength > maximumBytes) throw new Error('response_too_large')
    return JSON.parse(text) as T
  }
  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let bytes = 0
  let text = ''
  while (true) {
    let chunk: ReadableStreamReadResult<Uint8Array>
    try { chunk = await withDeadline(reader.read(), deadline) }
    catch (error) { await reader.cancel().catch(() => undefined); throw error }
    if (chunk.done) break
    bytes += chunk.value.byteLength
    if (bytes > maximumBytes) {
      await reader.cancel()
      throw new Error('response_too_large')
    }
    text += decoder.decode(chunk.value, { stream: true })
  }
  return JSON.parse(text + decoder.decode()) as T
}

async function withDeadline<T>(operation: Promise<T>, deadline: number): Promise<T> {
  const remaining = deadline - Date.now()
  if (remaining <= 0) throw new Error('response_timeout')
  let timer: ReturnType<typeof setTimeout> | undefined
  try {
    return await Promise.race([
      operation,
      new Promise<T>((_, reject) => { timer = setTimeout(() => reject(new Error('response_timeout')), remaining) }),
    ])
  } finally { if (timer) clearTimeout(timer) }
}