import { Alert, AlertDescription } from '../ui/alert'
import { Button } from '../ui/button'
import { Card, CardContent } from '../ui/card'
import { TesseraMark } from '../common/TesseraMark'

export type SignInError = 'not-allowed' | 'oidc-failed'

export interface SignInProps {
  onSignIn?: () => void
  error?: SignInError | null
  signedOut?: boolean
}

function MicrosoftLogo() {
  return (
    <svg viewBox="0 0 21 21" width="18" height="18" aria-hidden>
      <rect x="1" y="1" width="9" height="9" fill="#f25022" />
      <rect x="11" y="1" width="9" height="9" fill="#7fba00" />
      <rect x="1" y="11" width="9" height="9" fill="#00a4ef" />
      <rect x="11" y="11" width="9" height="9" fill="#ffb900" />
    </svg>
  )
}

const ERROR_COPY: Record<SignInError, string> = {
  'not-allowed': "That account isn't allowed here. Ask your operator to add you.",
  'oidc-failed': "Sign-in didn't complete. Try again.",
}

export function SignIn({ onSignIn, error = null, signedOut = false }: SignInProps) {
  return (
    <div className="flex min-h-screen items-center justify-center bg-surface px-4 py-12">
      <Card className="w-full max-w-md">
        <CardContent className="flex flex-col items-center gap-6 p-8 text-center">
          <div className="flex flex-col items-center gap-3">
            <TesseraMark className="h-12 w-12 text-accent" />
            <div className="space-y-1">
              <h1 className="text-2xl font-semibold tracking-tight">Tessera</h1>
              <p className="text-sm text-muted-foreground">
                Sign in to manage your connected accounts.
              </p>
            </div>
          </div>

          {error ? (
            <Alert variant="destructive" className="text-left">
              <AlertDescription className="text-foreground">{ERROR_COPY[error]}</AlertDescription>
            </Alert>
          ) : null}

          {signedOut && !error ? (
            <Alert className="text-left">
              <AlertDescription>You're signed out.</AlertDescription>
            </Alert>
          ) : null}

          <Button size="lg" className="w-full" onClick={onSignIn}>
            <MicrosoftLogo />
            Sign in with Microsoft
          </Button>

          <p className="text-xs leading-relaxed text-muted-foreground">
            Tessera never stores a password. Your sessions stay in your own vault.
          </p>
        </CardContent>
      </Card>
    </div>
  )
}
