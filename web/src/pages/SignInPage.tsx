import { useNavigate } from 'react-router-dom'
import { useSession } from '../app/session'
import { SignIn } from '../components/sign-in/SignIn'

export function SignInPage() {
  const { signIn } = useSession()
  const navigate = useNavigate()
  return (
    <SignIn
      onSignIn={() => {
        signIn()
        navigate('/accounts')
      }}
    />
  )
}
