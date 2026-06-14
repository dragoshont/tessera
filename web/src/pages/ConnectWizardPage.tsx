import { useNavigate } from 'react-router-dom'
import { useCreateConnection, usePeople, useRecipes, useTesseraClient } from '../api/hooks'
import { useSession } from '../app/session'
import { ConnectWizard } from '../components/connect/ConnectWizard'
import { useToast } from '../components/ui/toast'

const DRAFT_KEY = 'tessera.connect-draft'

export function ConnectWizardPage() {
  const navigate = useNavigate()
  const { currentUser } = useSession()
  const { toast } = useToast()
  const client = useTesseraClient()
  const { data: recipes = [], isLoading, isError, refetch } = useRecipes()
  const { data: people = [] } = usePeople()
  const createConnection = useCreateConnection()

  const isAdmin = currentUser?.role === 'Admin'
  const principal = currentUser?.principal ?? ''

  return (
    <ConnectWizard
      recipes={recipes}
      recipesLoading={isLoading}
      recipesError={isError}
      onRetryRecipes={() => void refetch()}
      currentPrincipal={principal}
      isAdmin={isAdmin}
      people={isAdmin ? people : undefined}
      requestSeed={(connectionId) => client.requestLiveView(connectionId)}
      createConnection={(input) => createConnection.mutateAsync(input)}
      onOpenHandoff={(connectionId) => navigate(`/handoff/${encodeURIComponent(connectionId)}`)}
      onCancel={() => navigate('/accounts')}
      onCreated={(connection) => {
        toast({
          variant: 'success',
          title: 'Account connected',
          description: `${connection.displayName} is connected for ${connection.ownerPrincipal}.`,
        })
        // Admins adding for someone else land on that person's detail; otherwise My accounts.
        if (isAdmin && connection.ownerPrincipal !== principal) {
          navigate(`/admin/users/${encodeURIComponent(connection.ownerPrincipal)}`)
        } else {
          navigate('/accounts')
        }
      }}
      persistKey={DRAFT_KEY}
    />
  )
}
