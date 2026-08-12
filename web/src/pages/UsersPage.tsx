import { useCurrentUser, usePeople } from '../api/hooks'
import { UsersList } from '../components/users/UsersList'

export function UsersPage() {
  const { data: people = [] } = usePeople()
  const { data: user } = useCurrentUser()

  return (
    <UsersList
      people={people}
      currentPrincipal={user?.principal}
    />
  )
}
