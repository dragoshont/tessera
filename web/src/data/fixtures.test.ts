import { describe, expect, it } from 'vitest'
import { connections, people } from './fixtures'
import type { ConnectionStatus } from './types'

describe('fixtures — people + connections contract', () => {
  it('classifies alice as Admin and the other two as Members', () => {
    const byPrincipal = Object.fromEntries(people.map((person) => [person.principal, person]))
    expect(byPrincipal['alice@example.com']?.role).toBe('Admin')
    expect(byPrincipal['bob@example.com']?.role).toBe('Member')
    expect(byPrincipal['carol@example.com']?.role).toBe('Member')
  })

  it('derives connectionCount from owned connections (the two views can never disagree)', () => {
    for (const person of people) {
      const owned = connections.filter(
        (connection) => connection.ownerPrincipal === person.principal,
      ).length
      expect(person.connectionCount).toBe(owned)
    }
  })

  it('mocks needsAttentionCount to 0 for the Phase 0 slice', () => {
    for (const person of people) {
      expect(person.needsAttentionCount).toBe(0)
    }
  })

  it('covers all four health states for alice', () => {
    const aliceStates = new Set<ConnectionStatus>(
      connections
        .filter((connection) => connection.ownerPrincipal === 'alice@example.com')
        .map((connection) => connection.status),
    )
    expect(aliceStates).toEqual(new Set<ConnectionStatus>(['live', 'expiring_soon', 'absent', 'error']))
  })

  it('never carries a raw secret value (presence flags only)', () => {
    for (const connection of connections) {
      const keys = Object.keys(connection)
      expect(keys).not.toContain('cookie')
      expect(keys).not.toContain('cookies')
      expect(keys).not.toContain('value')
      expect(keys).not.toContain('token')
      expect(keys).not.toContain('refreshToken')
      expect(keys).not.toContain('password')
    }
  })
})
