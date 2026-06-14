import { useNavigate } from 'react-router-dom'
import { useCurrentUser, usePeople } from '../api/hooks'
import { UsersList } from '../components/users/UsersList'

export function UsersPage() {
  const navigate = useNavigate()
  const { data: people = [] } = usePeople()
  const { data: user } = useCurrentUser()

  return (
    <UsersList
      people={people}
      currentPrincipal={user?.principal}
      onSelectPerson={(principal) => navigate(`/admin/users/${encodeURIComponent(principal)}`)}
    />
  )
}
