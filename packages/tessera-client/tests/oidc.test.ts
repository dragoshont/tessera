import { describe, expect, it, vi } from 'vitest'
import { requestOidcToken } from '../src/oidc'

describe('requestOidcToken', () => {
  it('posts to the exact discovered endpoint including its trailing slash', async () => {
    const send = vi.fn(async () => new Response(JSON.stringify({
      access_token: 'access',
      refresh_token: 'refresh',
      expires_in: 3600,
    }), { status: 200, headers: { 'Content-Type': 'application/json' } }))

    const token = await requestOidcToken(
      'https://auth.example/application/o/token/',
      { grant_type: 'authorization_code', client_id: 'client', code: 'code' },
      send,
    )

    expect(send).toHaveBeenCalledWith(
      'https://auth.example/application/o/token/',
      expect.objectContaining({ method: 'POST', redirect: 'error' }),
    )
    expect(token).toEqual({ accessToken: 'access', refreshToken: 'refresh', expiresIn: 3600 })
  })

  it('surfaces a bounded provider error code', async () => {
    const send = vi.fn(async () => new Response(JSON.stringify({ error: 'invalid_grant' }), {
      status: 400,
      headers: { 'Content-Type': 'application/json' },
    }))

    await expect(requestOidcToken(
      'https://auth.example/application/o/token/',
      { grant_type: 'authorization_code' },
      send,
    )).rejects.toThrow('oidc_token_invalid_grant')
  })

  it('rejects non-TLS and credential-bearing endpoints before sending', async () => {
    const send = vi.fn<typeof fetch>()
    await expect(requestOidcToken('http://auth.example/token/', {}, send))
      .rejects.toThrow('oidc_token_endpoint_invalid')
    await expect(requestOidcToken('https://user@auth.example/token/', {}, send))
      .rejects.toThrow('oidc_token_endpoint_invalid')
    expect(send).not.toHaveBeenCalled()
  })
})