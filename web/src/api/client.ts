import type { Connection, Person } from '../data/types'
import {
  connections as fixtureConnections,
  currentUserPrincipal as fixtureCurrentUserPrincipal,
  people as fixturePeople,
} from '../data/fixtures'

// The portal talks to the broker through this narrow, typed surface only. Phase 0
// ships an in-memory implementation over fixtures; a later phase swaps in an HTTP
// client that reads the real .NET `connection` projection — the views never change.
export interface TesseraClient {
  getCurrentUser(): Promise<Person>
  listPeople(): Promise<Person[]>
  listConnections(ownerPrincipal?: string): Promise<Connection[]>
  getConnection(connectionId: string): Promise<Connection | undefined>
}

export interface InMemorySeed {
  people?: Person[]
  connections?: Connection[]
  currentUserPrincipal?: string
}

const adminsFirst = (a: Person, b: Person): number => {
  if (a.role !== b.role) return a.role === 'Admin' ? -1 : 1
  return a.principal.localeCompare(b.principal)
}

/** Build a client over the given seed (defaults to the shipped fixtures). Tests
 *  and stories inject their own seed to exercise empty / mixed states. */
export function createInMemoryClient(seed: InMemorySeed = {}): TesseraClient {
  const people = seed.people ?? fixturePeople
  const connections = seed.connections ?? fixtureConnections
  const currentPrincipal = seed.currentUserPrincipal ?? fixtureCurrentUserPrincipal

  return {
    async getCurrentUser() {
      return people.find((person) => person.principal === currentPrincipal) ?? people[0]
    },
    async listPeople() {
      return [...people].sort(adminsFirst)
    },
    async listConnections(ownerPrincipal) {
      return connections.filter(
        (connection) => !ownerPrincipal || connection.ownerPrincipal === ownerPrincipal,
      )
    },
    async getConnection(connectionId) {
      return connections.find((connection) => connection.connectionId === connectionId)
    },
  }
}

export const tesseraClient = createInMemoryClient()
