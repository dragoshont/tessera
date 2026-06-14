import { useNavigate, useParams } from 'react-router-dom'
import { useConnections, useCurrentUser, useSchedule } from '../api/hooks'
import { AccountsTable } from '../components/accounts/AccountsTable'
import { ConnectionDrawer } from '../components/accounts/ConnectionDrawer'

export function AccountsPage() {
  const navigate = useNavigate()
  const { connectionId } = useParams()
  const { data: user } = useCurrentUser()
  const owner = user?.principal
  const { data: connections = [], isLoading, isError, refetch } = useConnections(owner)

  // Strictly self-scope: never render anyone else's connections (anti-pattern #6).
  const ownedConnections = owner ? connections : []
  const selected = ownedConnections.find((c) => c.connectionId === connectionId) ?? null
  // The selected connection's rotation schedule (ADR 0017) — "is an automatic job
  // keeping this session warm?" Fetched only while a connection is open.
  const { data: schedule } = useSchedule(selected?.connectionId)

  // Re-seed / Seed now launch the Live hand-off; the connectionId is encoded since
  // the broker keys it as "{provider}:{principal}".
  const toHandoff = (id: string) => navigate(`/handoff/${encodeURIComponent(id)}`)

  return (
    <>
      <AccountsTable
        connections={ownedConnections}
        ownerPrincipal={owner ?? ''}
        isLoading={isLoading || !owner}
        hasError={isError}
        onRetry={() => {
          void refetch()
        }}
        onSelectConnection={(id) => navigate(`/accounts/${id}`)}
        onConnectAccount={() => navigate('/connect')}
        onRowAction={(action, connection) => {
          if (action === 'reseed' || action === 'seed') toHandoff(connection.connectionId)
        }}
      />
      <ConnectionDrawer
        connection={selected}
        open={Boolean(connectionId)}
        schedule={schedule}
        onOpenChange={(open) => {
          if (!open) navigate('/accounts')
        }}
        onAction={(action, connection) => {
          if (action === 'reseed' || action === 'seed') toHandoff(connection.connectionId)
        }}
      />
    </>
  )
}
