import { readBoundedJsonResponse } from './http'

export type OidcToken = {
  accessToken: string
  refreshToken?: string
  expiresIn?: number
}

type OidcTokenBody = {
  access_token?: unknown
  refresh_token?: unknown
  expires_in?: unknown
  error?: unknown
}

export async function requestOidcToken(
  tokenEndpoint: string,
  parameters: Record<string, string>,
  send: typeof fetch = fetch,
): Promise<OidcToken> {
  const endpoint = new URL(tokenEndpoint)
  if (endpoint.protocol !== 'https:' || endpoint.username || endpoint.password)
    throw new Error('oidc_token_endpoint_invalid')
  const response = await send(tokenEndpoint, {
    method: 'POST',
    redirect: 'error',
    headers: { Accept: 'application/json', 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams(parameters).toString(),
  })
  const body = await readBoundedJsonResponse<OidcTokenBody>(response, 64 * 1024, 10_000)
  if (!response.ok || typeof body.access_token !== 'string' || body.access_token.length === 0) {
    const error = typeof body.error === 'string' && /^[a-z0-9_.-]{1,64}$/i.test(body.error)
      ? body.error.toLowerCase()
      : `http_${response.status}`
    throw new Error(`oidc_token_${error}`)
  }
  return {
    accessToken: body.access_token,
    refreshToken: typeof body.refresh_token === 'string' ? body.refresh_token : undefined,
    expiresIn: typeof body.expires_in === 'number' && Number.isFinite(body.expires_in)
      ? body.expires_in
      : undefined,
  }
}