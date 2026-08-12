import { createHash, randomBytes } from 'node:crypto'
import { shell } from 'electron'
import { AuthStore } from './auth-store'
import {
  AUTH_CALLBACK,
  validateAuthState,
  validateExternalUrl,
  validateOidcInput,
  type AuthState,
  type OidcInput,
} from './security'

interface Discovery {
  authorization_endpoint: string
  token_endpoint: string
}

export class OidcCoordinator {
  private waiter: { resolve: (auth: Extract<AuthState, { kind: 'oidc' }>) => void; reject: (error: Error) => void } | null = null

  constructor(private readonly store: AuthStore) {}

  async start(raw: OidcInput): Promise<Extract<AuthState, { kind: 'oidc' }>> {
    if (this.waiter) throw new Error('OIDC sign-in is already active.')
    const input = validateOidcInput(raw)
    const discovery = await discover(input.authority)
    const verifier = base64Url(randomBytes(48))
    const state = base64Url(randomBytes(32))
    const challenge = base64Url(createHash('sha256').update(verifier).digest())
    await this.store.savePending({
      state,
      verifier,
      tokenEndpoint: discovery.token_endpoint,
      clientId: input.clientId,
      scope: input.scope,
      expiresAt: new Date(Date.now() + 10 * 60_000).toISOString(),
    })
    const authorization = new URL(discovery.authorization_endpoint)
    authorization.search = new URLSearchParams({
      client_id: input.clientId,
      redirect_uri: AUTH_CALLBACK,
      response_type: 'code',
      scope: input.scope,
      state,
      code_challenge: challenge,
      code_challenge_method: 'S256',
    }).toString()
    await shell.openExternal(validateExternalUrl(authorization.href))
    return new Promise((resolve, reject) => {
      this.waiter = { resolve, reject }
      setTimeout(() => {
        if (this.waiter) {
          this.waiter = null
          reject(new Error('OIDC sign-in timed out.'))
        }
      }, 10 * 60_000).unref()
    })
  }

  async callback(url: URL): Promise<Extract<AuthState, { kind: 'oidc' }>> {
    const data = await this.store.load()
    const pending = data.pendingOidc
    if (!pending) throw new Error('OIDC callback has no pending request.')
    if (url.searchParams.get('state') !== pending.state) throw new Error('OIDC state does not match.')
    const code = url.searchParams.get('code')
    if (!code || code.length > 8192) throw new Error('OIDC callback code is invalid.')
    const response = await fetch(pending.tokenEndpoint, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded', Accept: 'application/json' },
      body: new URLSearchParams({
        grant_type: 'authorization_code',
        client_id: pending.clientId,
        redirect_uri: AUTH_CALLBACK,
        code,
        code_verifier: pending.verifier,
      }),
    })
    const text = await response.text()
    if (!response.ok || text.length > 256 * 1024) throw new Error('OIDC token exchange failed.')
    const body = JSON.parse(text) as { access_token?: unknown }
    const auth = validateAuthState({ kind: 'oidc', token: body.access_token }, true)
    if (!auth || auth.kind !== 'oidc') throw new Error('OIDC response has no access token.')
    await this.store.saveAuth(auth)
    await this.store.savePending(null)
    this.waiter?.resolve(auth)
    this.waiter = null
    return auth
  }
}

async function discover(authority: string): Promise<Discovery> {
  const url = new URL('.well-known/openid-configuration', authority.endsWith('/') ? authority : `${authority}/`)
  const response = await fetch(validateExternalUrl(url.href), { headers: { Accept: 'application/json' } })
  const text = await response.text()
  if (!response.ok || text.length > 256 * 1024) throw new Error('OIDC discovery failed.')
  const body = JSON.parse(text) as Partial<Discovery>
  if (!body.authorization_endpoint || !body.token_endpoint) throw new Error('OIDC discovery is incomplete.')
  return {
    authorization_endpoint: validateExternalUrl(body.authorization_endpoint),
    token_endpoint: validateExternalUrl(body.token_endpoint),
  }
}

function base64Url(value: Buffer): string {
  return value.toString('base64url')
}
