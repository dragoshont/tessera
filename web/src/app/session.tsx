import { createContext, useContext, useState, type ReactNode } from 'react'
import type { Person } from '../data/types'
import { people } from '../data/fixtures'

// Phase 0 sign-in is static: no real OIDC yet. The "Sign in with Microsoft"
// button sets the operator session; sign-out clears it. A later phase swaps this
// for an Entra confidential-client flow — the rest of the app is unaffected.
interface SessionContextValue {
  currentUser: Person | null
  signIn: () => void
  signOut: () => void
}

const SessionContext = createContext<SessionContextValue | null>(null)

const operator = people.find((person) => person.principal === 'alice@example.com') ?? people[0]

export function SessionProvider({
  children,
  initialUser = null,
}: {
  children: ReactNode
  initialUser?: Person | null
}) {
  const [currentUser, setCurrentUser] = useState<Person | null>(initialUser)

  const value: SessionContextValue = {
    currentUser,
    signIn: () => setCurrentUser(operator),
    signOut: () => setCurrentUser(null),
  }

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>
}

export function useSession(): SessionContextValue {
  const ctx = useContext(SessionContext)
  if (!ctx) throw new Error('useSession must be used within a SessionProvider')
  return ctx
}
