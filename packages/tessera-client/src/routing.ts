import type { ConnectionDiagnostics, RouteKind, ServerDescriptor } from './types'

export type RouteCandidate = { kind: RouteKind; origin: string; timeoutMs: number }
export type AuthLease = { accessToken?: string; isCurrent: () => boolean }
export type RouteManagerOptions = {
  expectedServerId: string
  clientVersion: string
  routes: RouteCandidate[]
  fetch?: typeof fetch
  now?: () => Date
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
const EMPTY_UUID = '00000000-0000-0000-0000-000000000000'
const VERSION = /^[0-9]+\.[0-9]+\.[0-9]+$/
const DESCRIPTOR_KEYS = ['apiVersion', 'displayName', 'product', 'protocolVersion', 'serverId', 'serverVersion']

export function parseServerDescriptor(value: unknown): ServerDescriptor {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('server_descriptor_invalid')
  const input = value as Record<string, unknown>
  if (Object.keys(input).sort().join('|') !== DESCRIPTOR_KEYS.join('|')) throw new Error('server_descriptor_invalid')
  if (input.product !== 'tessera' || input.apiVersion !== 'v1' || input.protocolVersion !== 1) throw new Error('server_incompatible')
  if (typeof input.serverId !== 'string' || !UUID.test(input.serverId) || input.serverId === EMPTY_UUID) throw new Error('server_descriptor_invalid')
  if (typeof input.displayName !== 'string' || input.displayName.length < 1 || input.displayName.length > 128 || /[\u0000-\u001f\u007f]/.test(input.displayName)) throw new Error('server_descriptor_invalid')
  if (typeof input.serverVersion !== 'string' || !VERSION.test(input.serverVersion)) throw new Error('server_descriptor_invalid')
  return input as ServerDescriptor
}

function verifiedOrigin(value: string): string {
  const url = new URL(value)
  if (url.protocol !== 'https:' && url.hostname !== '127.0.0.1' && url.hostname !== 'localhost') throw new Error('route_tls_required')
  if (url.username || url.password || url.search || url.hash || url.pathname !== '/') throw new Error('route_origin_invalid')
  return url.origin
}

export class RouteManager {
  private readonly options: RouteManagerOptions
  private readonly fetcher: typeof fetch
  private readonly clock: () => Date
  private selected: RouteCandidate | null = null
  private descriptor: ServerDescriptor | null = null
  private diagnosticsValue: ConnectionDiagnostics
  private connecting: Promise<ServerDescriptor> | null = null
  private generation = 0
  private readonly listeners = new Set<(diagnostics: ConnectionDiagnostics) => void>()

  constructor(options: RouteManagerOptions) {
    this.options = options
    if (!UUID.test(options.expectedServerId) || options.expectedServerId === EMPTY_UUID) throw new Error('expected_server_id_invalid')
    if (options.routes.length < 1 || options.routes.length > 4) throw new Error('route_count_invalid')
    const origins = new Set<string>()
    options.routes.forEach((route) => {
      route.origin = verifiedOrigin(route.origin)
      if (route.timeoutMs < 250 || route.timeoutMs > 10_000 || origins.has(route.origin)) throw new Error('route_invalid')
      origins.add(route.origin)
    })
    this.fetcher = options.fetch ?? fetch
    this.clock = options.now ?? (() => new Date())
    this.diagnosticsValue = {
      state: 'OFFLINE', route: null, latencyMs: null, serverId: null, serverVersion: null,
      clientVersion: options.clientVersion, lastSuccessfulConnection: null, failureCode: null,
    }
  }

  get diagnostics(): ConnectionDiagnostics { return { ...this.diagnosticsValue } }

  subscribe(listener: (diagnostics: ConnectionDiagnostics) => void): () => void {
    this.listeners.add(listener)
    listener(this.diagnostics)
    return () => this.listeners.delete(listener)
  }

  invalidate(failureCode = 'route_invalidated') {
    this.generation += 1
    this.connecting = null
    this.selected = null
    this.descriptor = null
    this.updateDiagnostics({ ...this.diagnosticsValue, state: 'OFFLINE', route: null, latencyMs: null, failureCode })
  }

  async ensureConnected(): Promise<ServerDescriptor> {
    return this.descriptor ?? this.connect()
  }

  async connect(): Promise<ServerDescriptor> {
    if (this.connecting) return this.connecting
    const generation = this.generation
    const operation = this.connectRoutes(generation)
    this.connecting = operation
    try { return await operation } finally { if (this.connecting === operation) this.connecting = null }
  }

  private async connectRoutes(generation: number): Promise<ServerDescriptor> {
    let failureCode = 'route_unavailable'
    for (const route of this.options.routes) {
      try {
        const started = Date.now()
        const descriptor = await this.probe(route)
        if (generation !== this.generation) throw new Error('route_invalidated')
        this.selected = route
        this.descriptor = descriptor
        this.updateDiagnostics({
          state: 'CONNECTED', route: route.kind, latencyMs: Math.max(0, Date.now() - started),
          serverId: descriptor.serverId, serverVersion: descriptor.serverVersion,
          clientVersion: this.options.clientVersion, lastSuccessfulConnection: this.clock().toISOString(), failureCode: null,
        })
        return descriptor
      } catch (error) {
        failureCode = error instanceof Error ? error.message : 'route_unavailable'
      }
    }
    if (generation !== this.generation) throw new Error('route_invalidated')
    this.selected = null
    this.descriptor = null
    this.updateDiagnostics({ ...this.diagnosticsValue, state: 'OFFLINE', route: null, latencyMs: null, failureCode })
    throw new Error(failureCode)
  }

  private updateDiagnostics(value: ConnectionDiagnostics) {
    this.diagnosticsValue = value
    for (const listener of this.listeners) listener(this.diagnostics)
  }

  private async probe(route: RouteCandidate): Promise<ServerDescriptor> {
    try {
      const response = await this.fetchWithTimeout(route, `${route.origin}/.well-known/tessera`, {
        method: 'GET', redirect: 'manual', headers: { Accept: 'application/json' },
      })
      if (response.status !== 200 || response.type === 'opaqueredirect') throw new Error(response.status === 503 ? 'server_identity_unconfigured' : 'server_unverified')
      const declaredLength = Number(response.headers.get('Content-Length'))
      if (Number.isFinite(declaredLength) && declaredLength > 4096) throw new Error('server_descriptor_too_large')
      const text = await readBoundedText(response, 4096, route.timeoutMs)
      const descriptor = parseServerDescriptor(JSON.parse(text))
      if (descriptor.serverId !== this.options.expectedServerId) throw new Error('server_identity_mismatch')
      return descriptor
    } catch (error) {
      if (error instanceof SyntaxError) throw new Error('server_descriptor_invalid')
      throw error
    }
  }

  async request(path: string, init: RequestInit = {}, accessToken?: string, isAuthCurrent: () => boolean = () => true): Promise<Response> {
    if (!path.startsWith('/')) throw new Error('api_path_invalid')
    await this.ensureConnected()
    const requestGeneration = this.generation
    if (!isAuthCurrent()) throw new Error('session_invalidated')
    const authSnapshot = accessToken
    const method = (init.method ?? 'GET').toUpperCase()
    const headers = new Headers(init.headers)
    if (authSnapshot) headers.set('Authorization', `Bearer ${authSnapshot}`)
    const requestInit = { ...init, method, headers, redirect: 'manual' as const }
    try {
      const route = this.selected!
      if (!isAuthCurrent()) throw new Error('session_invalidated')
      const response = await this.fetchWithTimeout(route, `${route.origin}${path}`, requestInit)
      if (requestGeneration !== this.generation) throw new Error('route_invalidated')
      if (!isAuthCurrent()) throw new Error('session_invalidated')
      if ([502, 503, 504].includes(response.status)) throw new Error('route_gateway_unavailable')
      return response
    } catch (firstError) {
      if (init.signal?.aborted) throw firstError
      if (!isAuthCurrent()) throw new Error('session_invalidated')
      if (requestGeneration !== this.generation) throw new Error('route_invalidated')
      const key = headers.get('Idempotency-Key')
      if (method !== 'GET' && method !== 'HEAD' && !key) throw firstError
      const alternate = this.options.routes.find((route) => route.origin !== this.selected?.origin)
      if (!alternate) throw firstError
      const descriptor = await this.probe(alternate)
      if (requestGeneration !== this.generation) throw new Error('route_invalidated')
      if (!isAuthCurrent()) throw new Error('session_invalidated')
      this.selected = alternate
      this.descriptor = descriptor
      this.updateDiagnostics({ ...this.diagnosticsValue, state: 'DEGRADED', route: alternate.kind, serverId: descriptor.serverId, serverVersion: descriptor.serverVersion, failureCode: 'route_failover' })
      const response = await this.fetchWithTimeout(alternate, `${alternate.origin}${path}`, requestInit)
      if (requestGeneration !== this.generation) throw new Error('route_invalidated')
      if (!isAuthCurrent()) throw new Error('session_invalidated')
      if ([502, 503, 504].includes(response.status)) throw new Error('route_gateway_unavailable')
      return response
    }
  }

  async requestAuthenticated(path: string, init: RequestInit, getAuthLease: () => Promise<AuthLease>): Promise<Response> {
    let authLease: AuthLease | undefined
    const method = (init.method ?? 'GET').toUpperCase()
    const canReplay = method === 'GET' || method === 'HEAD' || new Headers(init.headers).has('Idempotency-Key')
    for (let attempt = 0; attempt < 2; attempt += 1) {
      await this.ensureConnected()
      const generation = this.generation
      authLease ??= await getAuthLease()
      if (!authLease.isCurrent()) throw new Error('session_invalidated')
      if (generation !== this.generation || !this.selected || !this.descriptor) continue
      try { return await this.request(path, init, authLease.accessToken, authLease.isCurrent) }
      catch (error) {
        if (!(error instanceof Error) || error.message !== 'route_invalidated' || !canReplay || attempt === 1) throw error
      }
    }
    throw new Error('route_changed_during_authentication')
  }

  private async fetchWithTimeout(route: RouteCandidate, url: string, init: RequestInit, existingController?: AbortController): Promise<Response> {
    const controller = existingController ?? new AbortController()
    const externalSignal = init.signal
    const abortFromCaller = () => controller.abort()
    externalSignal?.addEventListener('abort', abortFromCaller, { once: true })
    const timer = setTimeout(() => controller.abort(), route.timeoutMs)
    try {
      return await this.fetcher(url, { ...init, signal: controller.signal })
    } catch (error) {
      if (controller.signal.aborted && !externalSignal?.aborted) throw new Error('route_timeout')
      throw error
    } finally {
      clearTimeout(timer)
      externalSignal?.removeEventListener('abort', abortFromCaller)
    }
  }
}

async function readBoundedText(response: Response, maximumBytes: number, timeoutMs: number): Promise<string> {
  const deadline = Date.now() + timeoutMs
  if (!response.body) {
    const text = await withDeadline(response.text(), deadline)
    if (new TextEncoder().encode(text).byteLength > maximumBytes) throw new Error('server_descriptor_too_large')
    return text
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
      throw new Error('server_descriptor_too_large')
    }
    text += decoder.decode(chunk.value, { stream: true })
  }
  return text + decoder.decode()
}

async function withDeadline<T>(operation: Promise<T>, deadline: number): Promise<T> {
  const remaining = deadline - Date.now()
  if (remaining <= 0) throw new Error('route_timeout')
  let timer: ReturnType<typeof setTimeout> | undefined
  try {
    return await Promise.race([
      operation,
      new Promise<T>((_, reject) => { timer = setTimeout(() => reject(new Error('route_timeout')), remaining) }),
    ])
  } finally { if (timer) clearTimeout(timer) }
}