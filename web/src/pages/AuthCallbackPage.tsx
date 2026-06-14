import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useSession } from '../app/session'
import { Button } from '../components/ui/button'
import { Card, CardContent } from '../components/ui/card'
import { TesseraMark } from '../components/common/TesseraMark'

// The OIDC redirect lands here. We wait for /portal/config to load (the session
// bootstrap fetches it), then complete the PKCE token exchange and load
// /portal/me. Success flips the session to authenticated and we route on; failure
// drops the operator back to a calm sign-in screen — never a raw OIDC error.
export function AuthCallbackPage() {
  const { config, status, completeOidcCallback } = useSession()
  const navigate = useNavigate()
  const [failed, setFailed] = useState(false)
  const started = useRef(false)

  useEffect(() => {
    if (status === 'authenticated') {
      navigate('/accounts', { replace: true })
      return
    }
    if (!config || started.current) return
    started.current = true
    void completeOidcCallback().catch(() => setFailed(true))
  }, [config, status, completeOidcCallback, navigate])

  return (
    <div className="flex min-h-screen items-center justify-center bg-surface px-4 py-12">
      <Card className="w-full max-w-md">
        <CardContent className="flex flex-col items-center gap-4 p-8 text-center">
          <TesseraMark className="h-10 w-10 text-accent" />
          {failed ? (
            <>
              <h1 className="text-lg font-semibold tracking-tight">Sign-in didn't complete</h1>
              <p className="text-sm text-muted-foreground">
                Something interrupted the Microsoft sign-in. You can try again.
              </p>
              <Button asChild className="mt-2">
                <Link to="/sign-in">Back to sign-in</Link>
              </Button>
            </>
          ) : (
            <>
              <h1 className="text-lg font-semibold tracking-tight">Completing sign-in…</h1>
              <p className="text-sm text-muted-foreground">Finishing the secure hand-off.</p>
            </>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
