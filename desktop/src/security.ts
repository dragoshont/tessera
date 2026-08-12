import net from 'node:net'

export const APP_SCHEME = 'app:'
export const APP_HOST = 'tessera'
export const APP_URL = 'app://tessera/'
export const API_ORIGIN = 'https://tessera.hont.ro'
export const AUTH_CALLBACK = 'tessera://auth/callback'
export const PRODUCT_ROUTES = new Set([
  '/chat',
  '/jobs',
  '/accounts',
  '/plugins',
  '/memory',
  '/activity',
  '/settings',
  '/sign-in',
])

const EXTERNAL_HOSTS = new Set([
  'auth.hont.ro',
  'accounts.google.com',
  'login.microsoftonline.com',
  'tessera.hont.ro',
])

export type AuthState =
  | { kind: 'dev'; principal: string }
  | { kind: 'oidc'; token: string }
  | null

export interface OidcInput {
  authority: string
  clientId: string
  scope: string
}

export function assertRendererUrl(value: string): void {
  const url = new URL(value)
  if (url.protocol !== APP_SCHEME || url.host !== APP_HOST)
    throw new Error('IPC sender is not the Tessera renderer.')
}

export function validateRoute(value: unknown): string {
  if (typeof value !== 'string' || !PRODUCT_ROUTES.has(value))
    throw new Error('Desktop route is not allowed.')
  return value
}

export function validateExternalUrl(value: unknown): string {
  if (typeof value !== 'string' || value.length > 4096)
    throw new Error('External URL is invalid.')
  const url = new URL(value)
  if (
    url.protocol !== 'https:' ||
    url.username !== '' ||
    url.password !== '' ||
    url.port !== '' ||
    net.isIP(url.hostname) !== 0 ||
    !EXTERNAL_HOSTS.has(url.hostname)
  ) throw new Error('External URL is not trusted.')
  return url.href
}

export function validateAuthState(value: unknown, allowToken: boolean): AuthState {
  if (value === null) return null
  if (!value || typeof value !== 'object') throw new Error('Auth state is invalid.')
  const candidate = value as Record<string, unknown>
  if (candidate.kind === 'dev') {
    if (typeof candidate.principal !== 'string' || candidate.principal.length > 320 || !candidate.principal.includes('@'))
      throw new Error('Developer principal is invalid.')
    return { kind: 'dev', principal: candidate.principal }
  }
  if (candidate.kind === 'oidc' && allowToken) {
    if (typeof candidate.token !== 'string' || candidate.token.length < 16 || candidate.token.length > 32768 || /[\u0000-\u001f\u007f]/.test(candidate.token))
      throw new Error('OIDC token is invalid.')
    return { kind: 'oidc', token: candidate.token }
  }
  throw new Error('Renderer cannot persist an OIDC token.')
}

export function validateOidcInput(value: unknown): OidcInput {
  if (!value || typeof value !== 'object') throw new Error('OIDC input is invalid.')
  const input = value as Record<string, unknown>
  if (typeof input.authority !== 'string' || typeof input.clientId !== 'string' || typeof input.scope !== 'string')
    throw new Error('OIDC input is incomplete.')
  const authority = validateExternalUrl(input.authority)
  const host = new URL(authority).hostname
  if (host !== 'auth.hont.ro' && host !== 'login.microsoftonline.com')
    throw new Error('OIDC authority is not trusted.')
  if (!/^[A-Za-z0-9._-]{3,256}$/.test(input.clientId)) throw new Error('OIDC client ID is invalid.')
  const scopes = input.scope.split(/\s+/).filter(Boolean)
  if (scopes.length === 0 || scopes.length > 16 || scopes.some((scope) => !/^[A-Za-z0-9:/.?_-]{1,256}$/.test(scope)))
    throw new Error('OIDC scopes are invalid.')
  return { authority, clientId: input.clientId, scope: scopes.join(' ') }
}

export function validateNotification(value: unknown): { title: string; body: string; route?: string } {
  if (!value || typeof value !== 'object') throw new Error('Notification is invalid.')
  const input = value as Record<string, unknown>
  if (typeof input.title !== 'string' || input.title.length < 1 || input.title.length > 120)
    throw new Error('Notification title is invalid.')
  if (typeof input.body !== 'string' || input.body.length < 1 || input.body.length > 500)
    throw new Error('Notification body is invalid.')
  const route = input.route === undefined ? undefined : validateRoute(input.route)
  return { title: input.title, body: input.body, route }
}

export function parseDeepLink(value: string): { kind: 'auth'; url: URL } | { kind: 'navigate'; route: string } {
  if (/\.\.|%2e/i.test(value)) throw new Error('Deep link traversal is invalid.')
  const url = new URL(value)
  if (url.protocol !== 'tessera:') throw new Error('Deep link scheme is invalid.')
  if (url.host === 'auth' && url.pathname === '/callback') return { kind: 'auth', url }
  if (url.host === 'open' && url.search === '' && url.hash === '')
    return { kind: 'navigate', route: validateRoute(url.pathname) }
  throw new Error('Deep link is not allowed.')
}
