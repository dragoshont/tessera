import { createContext, useContext, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { tesseraClient, type TesseraClient } from './client'
import type { CreateConnectionInput } from '../data/types'

const TesseraClientContext = createContext<TesseraClient>(tesseraClient)

export function TesseraClientProvider({
  client = tesseraClient,
  children,
}: {
  client?: TesseraClient
  children: ReactNode
}) {
  return <TesseraClientContext.Provider value={client}>{children}</TesseraClientContext.Provider>
}

export function useTesseraClient(): TesseraClient {
  return useContext(TesseraClientContext)
}

export function useCurrentUser() {
  const client = useTesseraClient()
  return useQuery({ queryKey: ['currentUser'], queryFn: () => client.getCurrentUser() })
}

export function usePeople() {
  const client = useTesseraClient()
  return useQuery({ queryKey: ['people'], queryFn: () => client.listPeople() })
}

export function useConnections(ownerPrincipal?: string) {
  const client = useTesseraClient()
  return useQuery({
    queryKey: ['connections', ownerPrincipal ?? 'all'],
    queryFn: () => client.listConnections(ownerPrincipal),
  })
}

export function useConnection(connectionId?: string) {
  const client = useTesseraClient()
  return useQuery({
    queryKey: ['connection', connectionId],
    queryFn: () => client.getConnection(connectionId as string),
    enabled: Boolean(connectionId),
  })
}

export function useRecipes() {
  const client = useTesseraClient()
  return useQuery({ queryKey: ['recipes'], queryFn: () => client.listRecipes() })
}

/** The connect-wizard write. On success the connections list is invalidated so the
 *  new binding appears in the table without a manual refresh. */
export function useCreateConnection() {
  const client = useTesseraClient()
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (input: CreateConnectionInput) => client.createConnection(input),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['connections'] })
    },
  })
}

// ── Awareness dashboard (ADR 0017) ──────────────────────────────────────────

/** The secret-free activity feed. `principal` undefined = self-scope (member) or,
 *  for an operator, everyone; an operator may pass a principal to scope to one. */
export function useActivity(principal?: string, limit?: number) {
  const client = useTesseraClient()
  return useQuery({
    queryKey: ['activity', principal ?? 'self', limit ?? 'all'],
    queryFn: () => client.getActivity(principal, limit),
  })
}

/** Who/what may act as a person (delegations). */
export function useDelegations(principal?: string) {
  const client = useTesseraClient()
  return useQuery({
    queryKey: ['delegations', principal ?? 'self'],
    queryFn: () => client.listDelegations(principal),
  })
}

/** The loaded connector catalog. */
export function useModules() {
  const client = useTesseraClient()
  return useQuery({ queryKey: ['modules'], queryFn: () => client.listModules() })
}

/** One connection's rotation schedule. */
export function useSchedule(connectionId?: string) {
  const client = useTesseraClient()
  return useQuery({
    queryKey: ['schedule', connectionId],
    queryFn: () => client.getSchedule(connectionId as string),
    enabled: Boolean(connectionId),
  })
}

// ── Pending writes (ADR 0023) ───────────────────────────────────────────────

/** Writes held for the signed-in person's out-of-band approval (self-scoped server-side). */
export function usePendingWrites() {
  const client = useTesseraClient()
  return useQuery({ queryKey: ['pendingWrites'], queryFn: () => client.getPendingWrites() })
}

/** Approve a held write (authorizes the caller to re-issue the exact request; does not perform it).
 *  Refetches the held set whether the decision lands or fails (e.g. an expired write 404s). */
export function useApprovePendingWrite() {
  const client = useTesseraClient()
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => client.approvePendingWrite(id),
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: ['pendingWrites'] })
    },
  })
}

/** Deny a held write (it will never be forwarded). Refetches the held set on success or failure. */
export function useDenyPendingWrite() {
  const client = useTesseraClient()
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => client.denyPendingWrite(id),
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: ['pendingWrites'] })
    },
  })
}
