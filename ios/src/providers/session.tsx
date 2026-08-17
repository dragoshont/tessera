import { createContext, type PropsWithChildren, useCallback, useContext, useEffect, useRef, useState } from 'react'
import { AppState } from 'react-native'
import * as AuthSession from 'expo-auth-session'
import * as Crypto from 'expo-crypto'
import * as LocalAuthentication from 'expo-local-authentication'
import * as Network from 'expo-network'
import * as SecureStore from 'expo-secure-store'
import { createHttpClient, GenerationFence, readBoundedJsonResponse, requestOidcToken, RouteManager, type AuthLease, type ConnectionDiagnostics, type ServerDescriptor } from '@tessera/client'

import { TesseraApi } from '@/lib/api'
import { runtimeConfig } from '@/lib/config'
import { unlockTransition, type UnlockAttempt } from '@/providers/unlock-state'

type OidcConfig = { authority: string; clientId: string; scope: string }
type PortalConfig = { authMode: 'dev' | 'oidc' | 'none'; devLoopback: boolean; oidc?: OidcConfig }
type Principal = { principal: string; role: 'Admin' | 'Member' }
type StoredSession = { accessToken: string; refreshToken?: string; expiresAt?: number }
type SessionStatus = 'booting' | 'offline' | 'anonymous' | 'authenticated'
type SessionContextValue = {
  status: SessionStatus
  locked: boolean
  lockEnabled: boolean
  principal: Principal | null
  descriptor: ServerDescriptor | null
  diagnostics: ConnectionDiagnostics
  api: TesseraApi
  signIn: () => Promise<void>
  signOut: () => Promise<void>
  unlock: () => Promise<void>
  reconnect: () => Promise<void>
  setLockEnabled: (enabled: boolean) => Promise<void>
}

const SESSION_KEY = 'tessera.session.v1'
const LOCK_KEY = 'tessera.app-lock.v1'
const storeOptions = { keychainAccessible: SecureStore.WHEN_UNLOCKED_THIS_DEVICE_ONLY }
const routeManager = new RouteManager(runtimeConfig)
const SessionContext = createContext<SessionContextValue | null>(null)

async function readSession(): Promise<StoredSession | null> {
  const value = await SecureStore.getItemAsync(SESSION_KEY, storeOptions)
  if (!value) return null
  try { return JSON.parse(value) as StoredSession } catch { await SecureStore.deleteItemAsync(SESSION_KEY); return null }
}

async function writeSession(value: StoredSession | null) {
  if (!value) return SecureStore.deleteItemAsync(SESSION_KEY)
  await SecureStore.setItemAsync(SESSION_KEY, JSON.stringify(value), storeOptions)
}

async function getPortalConfig() {
  const response = await routeManager.request('/portal/config', { headers: { Accept: 'application/json' } })
  if (!response.ok) throw new Error('portal_config_unavailable')
  return readBoundedJsonResponse<PortalConfig>(response, 64 * 1024, 5_000)
}

export function SessionProvider({ children }: PropsWithChildren) {
  const [status, setStatus] = useState<SessionStatus>('booting')
  const [locked, setLocked] = useState(false)
  const [lockEnabled, setLockEnabledState] = useState(true)
  const [principal, setPrincipal] = useState<Principal | null>(null)
  const [descriptor, setDescriptor] = useState<ServerDescriptor | null>(null)
  const [diagnostics, setDiagnostics] = useState(routeManager.diagnostics)
  const [portalConfig, setPortalConfig] = useState<PortalConfig | null>(null)
  const sessionRef = useRef<StoredSession | null>(null)
  const portalConfigRef = useRef<PortalConfig | null>(null)
  const refreshRef = useRef<Promise<StoredSession | null> | null>(null)
  const sessionFenceRef = useRef(new GenerationFence())
  const lockFenceRef = useRef(new GenerationFence())
  const unlockAttemptRef = useRef<UnlockAttempt | null>(null)
  const appStateRef = useRef(AppState.currentState)

  const ensureFreshSession = useCallback(async () => {
    const current = sessionRef.current
    if (!current || !current.expiresAt || current.expiresAt > Date.now() + 60_000) return current
    const oidc = portalConfigRef.current?.oidc
    if (!current.refreshToken || !oidc) return current
    const refreshToken = current.refreshToken
    if (!refreshRef.current) {
      const generation = sessionFenceRef.current.capture()
      refreshRef.current = (async () => {
        const discovery = await AuthSession.fetchDiscoveryAsync(oidc.authority)
        if (!discovery.tokenEndpoint) throw new Error('oidc_token_endpoint_missing')
        const token = await requestOidcToken(discovery.tokenEndpoint, {
          grant_type: 'refresh_token',
          client_id: oidc.clientId,
          refresh_token: refreshToken,
          scope: oidc.scope.split(/\s+/).filter(Boolean).join(' '),
        })
        const refreshed: StoredSession = {
          accessToken: token.accessToken,
          refreshToken: token.refreshToken ?? refreshToken,
          expiresAt: token.expiresIn ? Date.now() + token.expiresIn * 1000 : undefined,
        }
        if (!sessionFenceRef.current.isCurrent(generation) || sessionRef.current !== current) return null
        await writeSession(refreshed)
        if (!sessionFenceRef.current.isCurrent(generation) || sessionRef.current !== current) {
          await writeSession(null)
          return null
        }
        sessionRef.current = refreshed
        return refreshed
      })().finally(() => { refreshRef.current = null })
    }
    return refreshRef.current
  }, [])

  const getAuthLease = useCallback(async (): Promise<AuthLease> => {
    const generation = sessionFenceRef.current.capture()
    const session = await ensureFreshSession()
    return {
      accessToken: session?.accessToken,
      isCurrent: () => sessionFenceRef.current.isCurrent(generation) && sessionRef.current === session,
    }
  }, [ensureFreshSession])
  const apiRef = useRef<TesseraApi | null>(null)
  if (!apiRef.current) apiRef.current = new TesseraApi(createHttpClient({ routes: routeManager, getAuthLease, createIdempotencyKey: (prefix) => `${prefix}-${Crypto.randomUUID()}` }), routeManager, getAuthLease)

  const loadPrincipal = useCallback(async (session: StoredSession, generation = sessionFenceRef.current.capture()) => {
    const isCurrent = () => sessionFenceRef.current.isCurrent(generation) && sessionRef.current === session
    const response = await routeManager.request('/portal/me', { headers: { Accept: 'application/json' } }, session.accessToken, isCurrent)
    if (!isCurrent()) return
    if (response.status === 401 || response.status === 403) {
      sessionFenceRef.current.invalidate()
      await writeSession(null)
      sessionRef.current = null
      setPrincipal(null)
      setStatus('anonymous')
      return
    }
    if (!response.ok) throw new Error('principal_unavailable')
    const principal = await readBoundedJsonResponse<Principal>(response, 64 * 1024, 5_000)
    if (!isCurrent()) return
    setPrincipal(principal)
    setStatus('authenticated')
  }, [])

  const reconnect = useCallback(async () => {
    const generation = sessionFenceRef.current.capture()
    try {
      const connected = await routeManager.connect()
      if (!sessionFenceRef.current.isCurrent(generation)) return
      setDescriptor(connected)
      const config = await getPortalConfig()
      if (!sessionFenceRef.current.isCurrent(generation)) return
      setPortalConfig(config)
      portalConfigRef.current = config
      const session = await ensureFreshSession()
      if (!sessionFenceRef.current.isCurrent(generation)) return
      if (session) await loadPrincipal(session, generation)
      else setStatus('anonymous')
    } catch {
      if (!sessionFenceRef.current.isCurrent(generation)) return
      setDescriptor(null)
      setStatus('offline')
    }
  }, [ensureFreshSession, loadPrincipal])

  useEffect(() => {
    void (async () => {
      sessionRef.current = await readSession()
      const enabled = (await SecureStore.getItemAsync(LOCK_KEY)) !== 'false'
      setLockEnabledState(enabled)
      if (sessionRef.current && enabled) {
        lockFenceRef.current.invalidate()
        setLocked(true)
      }
      await reconnect()
    })()
  }, [reconnect])

  useEffect(() => {
    return routeManager.subscribe(setDiagnostics)
  }, [])

  useEffect(() => {
    const subscription = Network.addNetworkStateListener((state) => {
      routeManager.invalidate(state.isConnected ? 'network_changed' : 'network_offline')
      if (state.isConnected) void reconnect()
      else setStatus('offline')
    })
    return () => subscription.remove()
  }, [reconnect])

  useEffect(() => {
    const subscription = AppState.addEventListener('change', (state) => {
      appStateRef.current = state
      const attempt = unlockAttemptRef.current
      const transition = unlockTransition(
        state,
        attempt,
        attempt ? lockFenceRef.current.isCurrent(attempt.generation) : true,
        Date.now(),
      )
      if (transition === 'COMPLETE') {
        unlockAttemptRef.current = null
        setLocked(false)
        return
      }
      if (transition === 'IGNORE') return
      unlockAttemptRef.current = null
      if (lockEnabled && sessionRef.current) {
        lockFenceRef.current.invalidate()
        setLocked(true)
      }
    })
    return () => subscription.remove()
  }, [lockEnabled])

  const signIn = useCallback(async () => {
    if (!descriptor) throw new Error('server_unverified')
    const oidc = portalConfig?.oidc
    if (!oidc || portalConfig?.authMode !== 'oidc') throw new Error('oidc_unavailable')
    const discovery = await AuthSession.fetchDiscoveryAsync(oidc.authority)
    const redirectUri = AuthSession.makeRedirectUri({ scheme: 'tessera', path: 'auth/callback' })
    const request = new AuthSession.AuthRequest({
      clientId: oidc.clientId,
      redirectUri,
      responseType: AuthSession.ResponseType.Code,
      scopes: oidc.scope.split(/\s+/).filter(Boolean),
      usePKCE: true,
    })
    await request.makeAuthUrlAsync(discovery)
    const result = await request.promptAsync(discovery)
    if (result.type === 'error') {
      const code = result.params.error
      throw new Error(typeof code === 'string' && /^[a-z0-9_.-]{1,64}$/i.test(code)
        ? `oidc_authorize_${code.toLowerCase()}`
        : 'oidc_authorize_failed')
    }
    if (result.type !== 'success') throw new Error(`sign_in_${result.type}`)
    if (!result.params.code || !request.codeVerifier) throw new Error('oidc_authorize_response_invalid')
    if (!discovery.tokenEndpoint) throw new Error('oidc_token_endpoint_missing')
    const token = await requestOidcToken(discovery.tokenEndpoint, {
      grant_type: 'authorization_code',
      client_id: oidc.clientId,
      code: result.params.code,
      redirect_uri: redirectUri,
      code_verifier: request.codeVerifier,
    })
    const stored: StoredSession = {
      accessToken: token.accessToken,
      refreshToken: token.refreshToken,
      expiresAt: token.expiresIn ? Date.now() + token.expiresIn * 1000 : undefined,
    }
    sessionFenceRef.current.invalidate()
    await writeSession(stored)
    sessionRef.current = stored
    await loadPrincipal(stored)
  }, [descriptor, loadPrincipal, portalConfig])

  const signOut = useCallback(async () => {
    sessionFenceRef.current.invalidate()
    lockFenceRef.current.invalidate()
    unlockAttemptRef.current = null
    sessionRef.current = null
    await writeSession(null)
    setPrincipal(null)
    setLocked(false)
    setStatus(descriptor ? 'anonymous' : 'offline')
  }, [descriptor])

  const unlock = useCallback(async () => {
    if (!lockEnabled) { unlockAttemptRef.current = null; setLocked(false); return }
    const generation = lockFenceRef.current.capture()
    unlockAttemptRef.current = { generation, authenticatedAt: null }
    try {
      const result = await LocalAuthentication.authenticateAsync({ promptMessage: 'Unlock Tessera', fallbackLabel: 'Use Passcode' })
      if (!result.success || !lockFenceRef.current.isCurrent(generation)) return
      if (appStateRef.current === 'active') {
        unlockAttemptRef.current = null
        setLocked(false)
      } else if (appStateRef.current === 'inactive') {
        unlockAttemptRef.current = { generation, authenticatedAt: Date.now() }
      }
    } finally {
      if (unlockAttemptRef.current?.generation === generation && unlockAttemptRef.current.authenticatedAt === null)
        unlockAttemptRef.current = null
    }
  }, [lockEnabled])

  const setLockEnabled = useCallback(async (enabled: boolean) => {
    await SecureStore.setItemAsync(LOCK_KEY, String(enabled), storeOptions)
    lockFenceRef.current.invalidate()
    unlockAttemptRef.current = null
    setLockEnabledState(enabled)
    if (!enabled) setLocked(false)
  }, [])

  return <SessionContext.Provider value={{ status, locked, lockEnabled, principal, descriptor, diagnostics, api: apiRef.current, signIn, signOut, unlock, reconnect, setLockEnabled }}>{children}</SessionContext.Provider>
}

export function useSession() {
  const value = useContext(SessionContext)
  if (!value) throw new Error('useSession must be used within SessionProvider')
  return value
}