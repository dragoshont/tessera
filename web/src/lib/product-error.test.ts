import { describe, expect, it } from 'vitest'
import { recoveryMessage } from './product-error'

describe('product recovery copy', () => {
  it('explains failure, preservation, and recovery for provider authentication', () => {
    const message = recoveryMessage('provider_auth_required')
    expect(message).toContain('rejected')
    expect(message).toContain('preserved')
    expect(message).toContain('Reconnect')
  })

  it('keeps unknown codes actionable without exposing identifier formatting', () => {
    const message = recoveryMessage('new_dependency_failure')
    expect(message).toContain('new dependency failure')
    expect(message).toContain('preserved')
    expect(message).toContain('before retrying')
  })
})
