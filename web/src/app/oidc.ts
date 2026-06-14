import type { OidcConfig } from '../data/types'

// Real OIDC sign-in (Authorization Code + PKCE) via oidc-client-ts. The library
// is loaded with a dynamic import so it only enters the bundle in real OIDC
// deployments — the dev and demo paths never pull it in. oidc-client-ts performs
// the PKCE challenge and the token exchange on the callback; we hand the verified
// access token to the auth holder so the broker sees `Authorization: Bearer …`.

function redirectUri(): string {
  return `${window.location.origin}/auth/callback`
}

async function userManager(oidc: OidcConfig) {
  const { UserManager, WebStorageStateStore } = await import('oidc-client-ts')
  return new UserManager({
    authority: oidc.authority,
    client_id: oidc.clientId,
    redirect_uri: redirectUri(),
    post_logout_redirect_uri: `${window.location.origin}/sign-in`,
    response_type: 'code',
    scope: oidc.scope,
    // Tab-scoped storage, consistent with our sessionStorage auth holder.
    userStore: new WebStorageStateStore({ store: window.sessionStorage }),
    stateStore: new WebStorageStateStore({ store: window.sessionStorage }),
    automaticSilentRenew: false,
  })
}

/** Begin the Authorization Code + PKCE redirect to the IdP. Navigates away. */
export async function beginOidcSignIn(oidc: OidcConfig): Promise<void> {
  const manager = await userManager(oidc)
  await manager.signinRedirect()
}

/**
 * Complete the redirect callback (oidc-client-ts does the PKCE token exchange)
 * and return the verified access token. Throws if the IdP returned no token.
 */
export async function completeOidcSignIn(oidc: OidcConfig): Promise<string> {
  const manager = await userManager(oidc)
  const user = await manager.signinRedirectCallback()
  if (!user.access_token) throw new Error('OIDC callback returned no access token')
  return user.access_token
}
