import { useNavigate, useParams } from 'react-router-dom'
import { useConnections, useCurrentUser } from '../api/hooks'
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
      />
      <ConnectionDrawer
        connection={selected}
        open={Boolean(connectionId)}
        onOpenChange={(open) => {
          if (!open) navigate('/accounts')
        }}
      />
    </>
  )
}
