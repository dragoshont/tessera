import { useState, type FormEvent } from 'react'
import { Info } from 'lucide-react'
import type { PortalConfig, SignInError } from '../../data/types'
import { Alert, AlertDescription } from '../ui/alert'
import { Button } from '../ui/button'
import { Card, CardContent } from '../ui/card'
import { Input } from '../ui/input'
import { Label } from '../ui/label'
import { Skeleton } from '../ui/skeleton'
import { TesseraMark } from '../common/TesseraMark'

export type { SignInError }

export interface SignInProps {
  /** Sign-in config from `GET /portal/config`. `null` while it's loading. */
  config?: PortalConfig | null
  /** Loopback dev sign-in: submit a non-secret principal. */
  onDevSignIn?: (principal: string) => void
  /** OIDC: kick off the Microsoft redirect. */
  onMicrosoftSignIn?: () => void
  error?: SignInError | null
  signedOut?: boolean
  isSubmitting?: boolean
  /** Quick-pick principals for the dev card (generic identities only). */
  devSuggestions?: string[]
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

const DEFAULT_DEV_SUGGESTIONS = ['alice@example.com', 'bob@example.com', 'carol@example.com']

function MicrosoftButton({
  onClick,
  disabled,
}: {
  onClick?: () => void
  disabled?: boolean
}) {
  return (
    <Button
      size="lg"
      className="w-full"
      onClick={onClick}
      disabled={disabled}
      title={
        disabled
          ? 'Microsoft sign-in is used in real deployments. This is loopback dev mode.'
          : undefined
      }
    >
      <MicrosoftLogo />
      Sign in with Microsoft
    </Button>
  )
}

function DeveloperSignIn({
  onDevSignIn,
  isSubmitting,
  suggestions,
}: {
  onDevSignIn?: (principal: string) => void
  isSubmitting?: boolean
  suggestions: string[]
}) {
  const [principal, setPrincipal] = useState('')

  const submit = (event: FormEvent) => {
    event.preventDefault()
    const value = principal.trim()
    if (value) onDevSignIn?.(value)
  }

  return (
    <div className="flex w-full flex-col gap-4">
      <form onSubmit={submit} className="flex w-full flex-col gap-3 text-left">
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="dev-principal">Developer sign-in (local only)</Label>
          <Input
            id="dev-principal"
            name="principal"
            type="email"
            inputMode="email"
            autoComplete="off"
            placeholder="alice@example.com"
            value={principal}
            onChange={(event) => setPrincipal(event.target.value)}
          />
        </div>
        <div className="flex flex-wrap gap-2">
          {suggestions.map((suggestion) => (
            <Button
              key={suggestion}
              type="button"
              variant="outline"
              size="sm"
              onClick={() => setPrincipal(suggestion)}
            >
              {suggestion}
            </Button>
          ))}
        </div>
        <Button type="submit" className="w-full" disabled={isSubmitting || principal.trim() === ''}>
          Continue
        </Button>
      </form>

      <p className="text-xs leading-relaxed text-muted-foreground">
        Loopback dev mode — real deployments use Microsoft sign-in.
      </p>

      <MicrosoftButton disabled />
    </div>
  )
}

function SignInBody({
  config,
  onDevSignIn,
  onMicrosoftSignIn,
  isSubmitting,
  suggestions,
}: {
  config: PortalConfig | null | undefined
  onDevSignIn?: (principal: string) => void
  onMicrosoftSignIn?: () => void
  isSubmitting?: boolean
  suggestions: string[]
}) {
  if (config == null) {
    // Calm bootstrap — never a bare spinner.
    return (
      <div className="flex w-full flex-col items-center gap-3" aria-live="polite">
        <Skeleton className="h-10 w-full" />
        <p className="text-xs text-muted-foreground">Checking sign-in options…</p>
      </div>
    )
  }

  if (config.authMode === 'dev') {
    return (
      <DeveloperSignIn
        onDevSignIn={onDevSignIn}
        isSubmitting={isSubmitting}
        suggestions={suggestions}
      />
    )
  }

  if (config.authMode === 'none') {
    return (
      <Alert className="text-left">
        <AlertDescription className="flex items-start gap-2 text-foreground">
          <Info className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
          <span>
            Portal auth isn't configured yet. Set up Microsoft sign-in (or loopback dev mode) on the
            broker, then reload this page.
          </span>
        </AlertDescription>
      </Alert>
    )
  }

  // oidc
  return <MicrosoftButton onClick={onMicrosoftSignIn} disabled={isSubmitting} />
}

export function SignIn({
  config,
  onDevSignIn,
  onMicrosoftSignIn,
  error = null,
  signedOut = false,
  isSubmitting = false,
  devSuggestions = DEFAULT_DEV_SUGGESTIONS,
}: SignInProps) {
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

          <SignInBody
            config={config}
            onDevSignIn={onDevSignIn}
            onMicrosoftSignIn={onMicrosoftSignIn}
            isSubmitting={isSubmitting}
            suggestions={devSuggestions}
          />

          <p className="text-xs leading-relaxed text-muted-foreground">
            Tessera never stores a password. Your sessions stay in your own vault.
          </p>
        </CardContent>
      </Card>
    </div>
  )
}

