import type { AuthState } from './auth'
import type { OidcConfig } from '../data/types'

export const PRODUCT_ROUTES = [
  '/chat',
  '/jobs',
  '/accounts',
  '/plugins',
  '/memory',
  '/activity',
  '/settings',
  '/remote',
] as const

export type MacHostStatus = {
  available: boolean
  state: 'CLIENT_ONLY' | 'ENABLED' | 'DISABLED' | 'REQUIRES_APPROVAL' | 'NOT_FOUND' | 'UNAVAILABLE'
  bundleIdentifier: 'ro.hont.tessera.host'
}

export interface TesseraDesktopBridge {
  readonly platform: 'desktop'
  readonly version: string
  getApiOrigin(): Promise<string>
  loadAuth(): Promise<AuthState>
  saveAuth(value: AuthState): Promise<void>
  signInOidc(config: OidcConfig): Promise<Extract<AuthState, { kind: 'oidc' }>>
  openExternal(url: string): Promise<void>
  notify(input: { title: string; body: string; route?: string }): Promise<void>
  getMacHostStatus(): Promise<MacHostStatus>
  setMacHostEnabled(enabled: boolean): Promise<MacHostStatus>
  onNavigate(listener: (route: string) => void): () => void
}

declare global {
  interface Window {
    tesseraDesktop?: TesseraDesktopBridge
  }
}

let apiOrigin = ''

export function isDesktop(): boolean {
  return window.tesseraDesktop?.platform === 'desktop'
}

export async function initializeRuntime(): Promise<AuthState | undefined> {
  if (!window.tesseraDesktop) return undefined
  apiOrigin = normalizeOrigin(await window.tesseraDesktop.getApiOrigin())
  return window.tesseraDesktop.loadAuth()
}

export function apiUrl(path: string): string {
  if (!path.startsWith('/')) throw new Error('API path must be absolute.')
  return `${apiOrigin}${path}`
}

export function getApiOrigin(): string {
  return apiOrigin
}

export async function persistDesktopAuth(value: AuthState): Promise<void> {
  if (window.tesseraDesktop && value === null) await window.tesseraDesktop.saveAuth(value)
}

export async function signInDesktopOidc(
  config: OidcConfig,
): Promise<Extract<AuthState, { kind: 'oidc' }>> {
  if (!window.tesseraDesktop) throw new Error('Desktop OIDC is unavailable.')
  return window.tesseraDesktop.signInOidc(config)
}

export function subscribeDesktopNavigation(listener: (route: string) => void): () => void {
  return window.tesseraDesktop?.onNavigate(listener) ?? (() => undefined)
}

export async function notifyDesktop(input: {
  title: string
  body: string
  route?: string
}): Promise<void> {
  if (window.tesseraDesktop) await window.tesseraDesktop.notify(input)
}

export function getMacHostStatus(): Promise<MacHostStatus> {
  if (!window.tesseraDesktop) return Promise.resolve({ available: false, state: 'CLIENT_ONLY', bundleIdentifier: 'ro.hont.tessera.host' })
  return window.tesseraDesktop.getMacHostStatus()
}

export function setMacHostEnabled(enabled: boolean): Promise<MacHostStatus> {
  if (!window.tesseraDesktop) throw new Error('Mac Host mode is available only in packaged Tessera.')
  return window.tesseraDesktop.setMacHostEnabled(enabled)
}

export async function openTrustedExternal(url: string): Promise<void> {
  const parsed = new URL(url)
  if (parsed.protocol !== 'https:') throw new Error('Only HTTPS links may leave Tessera.')
  if (window.tesseraDesktop) await window.tesseraDesktop.openExternal(parsed.href)
  else window.open(parsed.href, '_blank', 'noopener,noreferrer')
}

function normalizeOrigin(value: string): string {
  const parsed = new URL(value)
  if (parsed.protocol !== 'https:' && parsed.hostname !== '127.0.0.1' && parsed.hostname !== 'localhost')
    throw new Error('Desktop Tessera requires an HTTPS API origin.')
  return parsed.origin
}