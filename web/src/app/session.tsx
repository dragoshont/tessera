import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react'
import type { Person, PortalConfig, SignInError } from '../data/types'
import { HttpError } from '../api/client'
import { useTesseraClient } from '../api/hooks'
import { clearAuthState, getAuthState, setAuthState } from './auth'
import { beginOidcSignIn, completeOidcSignIn } from './oidc'

// The real operator session, backed by GET /portal/config + GET /portal/me.
//   - loading:       bootstrapping (fetching config, probing /portal/me)
//   - anonymous:     no valid credential — show SignIn
//   - authenticated: /portal/me returned the principal + role
// The chosen dev principal / OIDC token is persisted (sessionStorage, via the
// auth holder) so a refresh keeps the session and feeds the HTTP client's auth
// header. The whole app keys off this: admin nav and the identity chip read the
// real principal + role; never a fixture.
export type SessionStatus = 'loading' | 'anonymous' | 'authenticated'

interface SessionContextValue {
  status: SessionStatus
  config: PortalConfig | null
  currentUser: Person | null
  error: SignInError | null
  signedOut: boolean
  /** Loopback dev sign-in: trust a non-secret principal, then load /portal/me. */
  signInDev: (principal: string) => Promise<void>
  /** OIDC sign-in: redirect to the IdP (Authorization Code + PKCE). */
  signInOidc: () => Promise<void>
  /** Finish the OIDC redirect callback: exchange the code, then load /portal/me. */
  completeOidcCallback: () => Promise<void>
  signOut: () => void
}

const SessionContext = createContext<SessionContextValue | null>(null)

export function SessionProvider({
  children,
  initialUser = null,
}: {
  children: ReactNode
  /** Stories/tests pass a user to render an authenticated shell with no network
   *  bootstrap. The running app passes nothing → the real /portal/me bootstrap. */
  initialUser?: Person | null
}) {
  const client = useTesseraClient()
  const storyMode = initialUser != null

  const [status, setStatus] = useState<SessionStatus>(storyMode ? 'authenticated' : 'loading')
  const [config, setConfig] = useState<PortalConfig | null>(
    storyMode ? { authMode: 'dev', devLoopback: true } : null,
  )
  const [currentUser, setCurrentUser] = useState<Person | null>(initialUser)
  const [error, setError] = useState<SignInError | null>(null)
  const [signedOut, setSignedOut] = useState(false)

  // Bootstrap once. A ref guard (not an abort flag) keeps React 19 StrictMode's
  // double-invoke from running the probe twice while still letting the single run
  // complete and commit its state.
  const bootstrapped = useRef(false)
  useEffect(() => {
    if (storyMode || bootstrapped.current) return
    bootstrapped.current = true

    void (async () => {
      let resolved: PortalConfig
      try {
        resolved = await client.getConfig()
      } catch {
        // Can't reach the broker config — present a calm "not configured" surface
        // rather than a scary error; sign-in simply isn't offered.
        resolved = { authMode: 'none', devLoopback: false }
      }
      setConfig(resolved)

      // Never call /portal/me unauthenticated; with no stored credential we are
      // simply anonymous and the SignIn surface takes over.
      if (!getAuthState()) {
        setStatus('anonymous')
        return
      }
      try {
        const me = await client.getCurrentUser()
        setCurrentUser(me)
        setStatus('authenticated')
      } catch {
        // A stale/expired credential → drop it and fall back to sign-in, calmly.
        clearAuthState()
        setStatus('anonymous')
      }
    })()
  }, [client, storyMode])

  const loadMe = useCallback(async () => {
    const me = await client.getCurrentUser()
    setCurrentUser(me)
    setStatus('authenticated')
  }, [client])

  const signInDev = useCallback(
    async (principal: string) => {
      const trimmed = principal.trim()
      if (!trimmed) return
      setError(null)
      setSignedOut(false)
      setAuthState({ kind: 'dev', principal: trimmed })
      try {
        await loadMe()
      } catch (err) {
        clearAuthState()
        setStatus('anonymous')
        setError(err instanceof HttpError && err.status === 403 ? 'not-allowed' : 'oidc-failed')
      }
    },
    [loadMe],
  )

  const signInOidc = useCallback(async () => {
    setError(null)
    setSignedOut(false)
    if (!config?.oidc) {
      setError('oidc-failed')
      return
    }
    try {
      await beginOidcSignIn(config.oidc) // navigates away to the IdP
    } catch {
      setError('oidc-failed')
    }
  }, [config])

  const completeOidcCallback = useCallback(async () => {
    if (!config?.oidc) throw new Error('OIDC config is not ready')
    const token = await completeOidcSignIn(config.oidc)
    setAuthState({ kind: 'oidc', token })
    await loadMe()
  }, [config, loadMe])

  const signOut = useCallback(() => {
    clearAuthState()
    setCurrentUser(null)
    setStatus('anonymous')
    setSignedOut(true)
    setError(null)
  }, [])

  const value = useMemo<SessionContextValue>(
    () => ({
      status,
      config,
      currentUser,
      error,
      signedOut,
      signInDev,
      signInOidc,
      completeOidcCallback,
      signOut,
    }),
    [
      status,
      config,
      currentUser,
      error,
      signedOut,
      signInDev,
      signInOidc,
      completeOidcCallback,
      signOut,
    ],
  )

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>
}

export function useSession(): SessionContextValue {
  const ctx = useContext(SessionContext)
  if (!ctx) throw new Error('useSession must be used within a SessionProvider')
  return ctx
}

