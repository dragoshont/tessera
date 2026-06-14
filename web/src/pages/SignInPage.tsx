import { useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { useSession } from '../app/session'
import { SignIn } from '../components/sign-in/SignIn'

export function SignInPage() {
  const { status, config, error, signedOut, signInDev, signInOidc } = useSession()
  const navigate = useNavigate()

  // Once /portal/me resolves (dev submit or OIDC callback), leave the sign-in screen.
  useEffect(() => {
    if (status === 'authenticated') navigate('/accounts', { replace: true })
  }, [status, navigate])

  return (
    <SignIn
      config={config}
      error={error}
      signedOut={signedOut}
      onDevSignIn={(principal) => void signInDev(principal)}
      onMicrosoftSignIn={() => void signInOidc()}
    />
  )
}
