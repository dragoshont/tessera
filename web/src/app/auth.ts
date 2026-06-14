// The portal's current credential lives here as a tiny module-level holder so the
// HTTP client's `authHeader()` can read it per request without a token ever being
// baked into code. It is persisted to sessionStorage so a refresh keeps the
// session within the tab; sign-out clears it. Two shapes only:
//   - dev loopback: a non-secret principal, sent as `X-Tessera-Dev-Principal`
//   - oidc: a verified access token, sent as `Authorization: Bearer …`
export type AuthState =
  | { kind: 'dev'; principal: string }
  | { kind: 'oidc'; token: string }
  | null

const STORAGE_KEY = 'tessera.auth'

function readPersisted(): AuthState {
  if (typeof window === 'undefined') return null
  try {
    const raw = window.sessionStorage.getItem(STORAGE_KEY)
    if (!raw) return null
    const parsed = JSON.parse(raw) as AuthState
    if (parsed && (parsed.kind === 'dev' || parsed.kind === 'oidc')) return parsed
    return null
  } catch {
    return null
  }
}

let current: AuthState = readPersisted()

function persist(next: AuthState): void {
  if (typeof window === 'undefined') return
  try {
    if (next) window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify(next))
    else window.sessionStorage.removeItem(STORAGE_KEY)
  } catch {
    // sessionStorage can be unavailable (e.g. private mode) — auth simply won't
    // persist across refreshes, which is an acceptable degradation.
  }
}

export function getAuthState(): AuthState {
  return current
}

export function setAuthState(next: AuthState): void {
  current = next
  persist(next)
}

export function clearAuthState(): void {
  setAuthState(null)
}

/** The per-request auth headers for the HTTP client (never a baked-in token). */
export function authHeader(): Record<string, string> {
  if (!current) return {}
  if (current.kind === 'dev') return { 'X-Tessera-Dev-Principal': current.principal }
  return { Authorization: `Bearer ${current.token}` }
}
