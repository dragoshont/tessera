import { createContext, useContext, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { tesseraClient, type TesseraClient } from './client'

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
