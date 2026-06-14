import { useEffect, useMemo, useState } from 'react'
import {
  ArrowRight,
  Check,
  ChevronLeft,
  Info,
  KeyRound,
  RotateCw,
  Search,
  X,
} from 'lucide-react'
import type {
  Connection,
  CreateConnectionInput,
  LiveViewHandle,
  LiveViewResult,
  Person,
  Recipe,
} from '../../data/types'
import { HttpError } from '../../api/client'
import { cn } from '../../lib/utils'
import { Alert, AlertDescription, AlertTitle } from '../ui/alert'
import { Button } from '../ui/button'
import { Card, CardContent } from '../ui/card'
import { Input } from '../ui/input'
import { Label } from '../ui/label'
import { Skeleton } from '../ui/skeleton'
import { ProviderIcon } from '../common/ProviderIcon'

// The connect-account wizard: one decision per step, resumable-friendly. The
// component is presentational — it takes recipes + the signed-in identity and a
// pair of async actions (seed attempt, create) and drives the 5 steps. Stories
// inject `initialState` to render any step/seed/error without a backend.
export type WizardStep = 'provider' | 'person' | 'credential' | 'seed' | 'finish'

interface WizardDraft {
  /** Provider slug the broker keys on (e.g. "health"). */
  provider: string
  /** Human label for the chosen provider (e.g. "Health Portal"). */
  displayName: string
  /** The principal the connection acts as. */
  principal: string
  /** The NAME of the vault secret holding the session bundle — never a value. */
  credential: string
}

type SeedPhase = 'idle' | 'loading' | 'unavailable' | 'ready'

interface SeedState {
  phase: SeedPhase
  reason?: string
  handle?: LiveViewHandle
}

export interface ConnectWizardProps {
  recipes: Recipe[]
  recipesLoading?: boolean
  recipesError?: boolean
  onRetryRecipes?: () => void
  /** The signed-in operator's principal (the default connection owner). */
  currentPrincipal: string
  /** Admins may connect on behalf of another person; members are locked to self. */
  isAdmin: boolean
  /** Quick-pick people for admins (omit for members). */
  people?: Person[]
  /** Attempt to mint a Live hand-off (normally 503 → unavailable right now). */
  requestSeed: (connectionId: string) => Promise<LiveViewResult>
  /** The write: POST /portal/connections. Rejects with HttpError on 403/400. */
  createConnection: (input: CreateConnectionInput) => Promise<Connection>
  onCreated: (connection: Connection) => void
  onCancel: () => void
  /** Open the full Live hand-off stage (only offered when a handle was minted). */
  onOpenHandoff?: (connectionId: string) => void
  /** When set, the in-progress draft is persisted here (sessionStorage) so a
   *  refresh resumes where the operator left off. */
  persistKey?: string
  /** Story/test-only seam to render a specific step/seed/error. Never used by the app. */
  initialState?: {
    step?: WizardStep
    draft?: Partial<WizardDraft>
    seed?: SeedState
    submitError?: string
  }
}

const STEPS: { key: WizardStep; label: string }[] = [
  { key: 'provider', label: 'Provider' },
  { key: 'person', label: 'Person' },
  { key: 'credential', label: 'Credential' },
  { key: 'seed', label: 'Seed' },
  { key: 'finish', label: 'Finish' },
]

interface PersistedDraft {
  step: WizardStep
  draft: WizardDraft
}

function readPersisted(key: string): PersistedDraft | null {
  if (typeof window === 'undefined') return null
  try {
    const raw = window.sessionStorage.getItem(key)
    return raw ? (JSON.parse(raw) as PersistedDraft) : null
  } catch {
    return null
  }
}

function writePersisted(key: string, value: PersistedDraft): void {
  if (typeof window === 'undefined') return
  try {
    window.sessionStorage.setItem(key, JSON.stringify(value))
  } catch {
    // Best-effort resume only.
  }
}

function clearPersisted(key: string): void {
  if (typeof window === 'undefined') return
  try {
    window.sessionStorage.removeItem(key)
  } catch {
    // ignore
  }
}

function Stepper({ current }: { current: WizardStep }) {
  const currentIndex = STEPS.findIndex((step) => step.key === current)
  return (
    <ol className="flex items-center gap-1.5" aria-label="Progress">
      {STEPS.map((step, index) => {
        const done = index < currentIndex
        const active = index === currentIndex
        return (
          <li key={step.key} className="flex flex-1 items-center gap-1.5">
            <span
              className={cn(
                'flex h-6 w-6 shrink-0 items-center justify-center rounded-full border text-xs font-medium',
                done && 'border-accent bg-accent text-accent-foreground',
                active && 'border-accent text-accent',
                !done && !active && 'border-border text-muted-foreground',
              )}
              aria-current={active ? 'step' : undefined}
            >
              {done ? <Check className="h-3.5 w-3.5" aria-hidden /> : index + 1}
            </span>
            <span
              className={cn(
                'hidden text-xs font-medium sm:block',
                active ? 'text-foreground' : 'text-muted-foreground',
              )}
            >
              {step.label}
            </span>
            {index < STEPS.length - 1 ? (
              <span className="mx-1 h-px flex-1 bg-border" aria-hidden />
            ) : null}
          </li>
        )
      })}
    </ol>
  )
}

export function ConnectWizard({
  recipes,
  recipesLoading = false,
  recipesError = false,
  onRetryRecipes,
  currentPrincipal,
  isAdmin,
  people,
  requestSeed,
  createConnection,
  onCreated,
  onCancel,
  onOpenHandoff,
  persistKey,
  initialState,
}: ConnectWizardProps) {
  const persisted = useMemo(
    () => (persistKey && !initialState ? readPersisted(persistKey) : null),
    [persistKey, initialState],
  )

  const [step, setStep] = useState<WizardStep>(
    initialState?.step ?? persisted?.step ?? 'provider',
  )
  const [draft, setDraft] = useState<WizardDraft>({
    provider: '',
    displayName: '',
    principal: currentPrincipal,
    credential: '',
    ...persisted?.draft,
    ...initialState?.draft,
  })
  const [seed, setSeed] = useState<SeedState>(initialState?.seed ?? { phase: 'idle' })
  const [submitting, setSubmitting] = useState(false)
  const [submitError, setSubmitError] = useState<string | null>(initialState?.submitError ?? null)
  const [search, setSearch] = useState('')

  // Persist progress for refresh-resume (no-op in stories, which set initialState).
  useEffect(() => {
    if (!persistKey || initialState) return
    writePersisted(persistKey, { step, draft })
  }, [persistKey, initialState, step, draft])

  const connectionId = `${draft.provider}:${draft.principal}`
  const isSelf = draft.principal.trim() === currentPrincipal
  const localPart = (draft.principal.split('@')[0] || 'user').replace(/[^a-z0-9]+/gi, '-')
  const credentialSuggestion = draft.provider ? `${draft.provider}-${localPart}-session` : ''

  const filteredRecipes = useMemo(() => {
    const query = search.trim().toLowerCase()
    if (!query) return recipes
    return recipes.filter(
      (recipe) =>
        recipe.displayName.toLowerCase().includes(query) ||
        recipe.provider.toLowerCase().includes(query),
    )
  }, [recipes, search])

  const canAdvance: Record<WizardStep, boolean> = {
    provider: draft.provider !== '',
    person: draft.principal.trim() !== '',
    credential: draft.credential.trim() !== '',
    seed: true,
    finish: true,
  }

  const goTo = (next: WizardStep) => {
    setSubmitError(null)
    setStep(next)
  }
  const stepIndex = STEPS.findIndex((entry) => entry.key === step)
  const goNext = () => {
    const next = STEPS[stepIndex + 1]
    if (next && canAdvance[step]) goTo(next.key)
  }
  const goBack = () => {
    const prev = STEPS[stepIndex - 1]
    if (prev) goTo(prev.key)
  }

  const cancel = () => {
    if (persistKey) clearPersisted(persistKey)
    onCancel()
  }

  const handleSeed = async () => {
    setSeed({ phase: 'loading' })
    try {
      const result = await requestSeed(connectionId)
      if ('handle' in result) setSeed({ phase: 'ready', handle: result.handle })
      else setSeed({ phase: 'unavailable', reason: result.unavailable })
    } catch {
      setSeed({
        phase: 'unavailable',
        reason: 'Couldn’t reach the broker to start seeding right now.',
      })
    }
  }

  const handleCreate = async () => {
    setSubmitting(true)
    setSubmitError(null)
    try {
      const connection = await createConnection({
        provider: draft.provider,
        principal: draft.principal.trim(),
        credential: draft.credential.trim(),
      })
      if (persistKey) clearPersisted(persistKey)
      onCreated(connection)
    } catch (err) {
      setSubmitError(
        err instanceof HttpError && err.status === 403
          ? 'You don’t have permission to connect this account for that person.'
          : err instanceof HttpError && err.status === 400
            ? 'That didn’t look right. Check the provider and person, then try again.'
            : 'Couldn’t create the connection. Please try again.',
      )
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Card className="mx-auto w-full max-w-2xl">
      <div className="flex items-center justify-between border-b border-border px-6 py-4">
        <h1 className="text-lg font-semibold tracking-tight">Connect an account</h1>
        <Button variant="ghost" size="icon" onClick={cancel} aria-label="Cancel">
          <X className="h-4 w-4" />
        </Button>
      </div>

      <div className="border-b border-border px-6 py-4">
        <Stepper current={step} />
      </div>

      <CardContent className="flex flex-col gap-5 p-6">
        {step === 'provider' ? (
          <ProviderStep
            recipes={filteredRecipes}
            loading={recipesLoading}
            error={recipesError}
            onRetry={onRetryRecipes}
            search={search}
            onSearch={setSearch}
            selected={draft.provider}
            onSelect={(recipe) =>
              setDraft((current) => ({
                ...current,
                provider: recipe.provider,
                displayName: recipe.displayName,
              }))
            }
          />
        ) : null}

        {step === 'person' ? (
          <PersonStep
            isAdmin={isAdmin}
            people={people}
            currentPrincipal={currentPrincipal}
            principal={draft.principal}
            isSelf={isSelf}
            onPrincipal={(principal) => setDraft((current) => ({ ...current, principal }))}
          />
        ) : null}

        {step === 'credential' ? (
          <CredentialStep
            value={draft.credential}
            suggestion={credentialSuggestion}
            onChange={(credential) => setDraft((current) => ({ ...current, credential }))}
          />
        ) : null}

        {step === 'seed' ? (
          <SeedStep
            displayName={draft.displayName}
            seed={seed}
            onSeed={() => void handleSeed()}
            onOpenHandoff={onOpenHandoff ? () => onOpenHandoff(connectionId) : undefined}
          />
        ) : null}

        {step === 'finish' ? (
          <FinishStep
            displayName={draft.displayName}
            principal={draft.principal}
            isSelf={isSelf}
            credential={draft.credential}
            seeded={seed.phase === 'ready'}
            submitError={submitError}
          />
        ) : null}
      </CardContent>

      <div className="flex items-center justify-between border-t border-border px-6 py-4">
        {stepIndex > 0 ? (
          <Button variant="ghost" onClick={goBack}>
            <ChevronLeft className="h-4 w-4" />
            Back
          </Button>
        ) : (
          <Button variant="ghost" onClick={cancel}>
            Cancel
          </Button>
        )}

        {step === 'finish' ? (
          <Button onClick={() => void handleCreate()} disabled={submitting}>
            {submitting ? 'Connecting…' : 'Connect'}
          </Button>
        ) : step === 'seed' ? (
          <Button onClick={goNext} variant={seed.phase === 'ready' ? 'default' : 'outline'}>
            {seed.phase === 'ready' ? 'Continue' : "Skip — I'll seed later"}
            <ArrowRight className="h-4 w-4" />
          </Button>
        ) : (
          <Button onClick={goNext} disabled={!canAdvance[step]}>
            Next
            <ArrowRight className="h-4 w-4" />
          </Button>
        )}
      </div>
    </Card>
  )
}

function ProviderStep({
  recipes,
  loading,
  error,
  onRetry,
  search,
  onSearch,
  selected,
  onSelect,
}: {
  recipes: Recipe[]
  loading: boolean
  error: boolean
  onRetry?: () => void
  search: string
  onSearch: (value: string) => void
  selected: string
  onSelect: (recipe: Recipe) => void
}) {
  return (
    <div className="flex flex-col gap-4">
      <div className="space-y-1">
        <h2 className="text-base font-medium">Which account?</h2>
        <p className="text-sm text-muted-foreground">Pick the provider you want to connect.</p>
      </div>

      <div className="relative">
        <Search
          className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
          aria-hidden
        />
        <Input
          value={search}
          onChange={(event) => onSearch(event.target.value)}
          placeholder="Search providers…"
          aria-label="Search providers"
          className="pl-9"
        />
      </div>

      {loading ? (
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-3">
          {Array.from({ length: 4 }).map((_, index) => (
            <Skeleton key={index} className="h-20 rounded-xl" />
          ))}
        </div>
      ) : error ? (
        <Alert variant="destructive" className="text-left">
          <AlertDescription className="flex items-center justify-between gap-3 text-foreground">
            <span>Couldn't load providers.</span>
            {onRetry ? (
              <Button size="sm" variant="outline" onClick={onRetry}>
                Retry
              </Button>
            ) : null}
          </AlertDescription>
        </Alert>
      ) : recipes.length === 0 ? (
        <p className="py-6 text-center text-sm text-muted-foreground">No matching providers.</p>
      ) : (
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-3">
          {recipes.map((recipe) => {
            const isSelected = recipe.provider === selected
            return (
              <button
                key={recipe.provider}
                type="button"
                onClick={() => onSelect(recipe)}
                aria-pressed={isSelected}
                className={cn(
                  'flex flex-col items-center gap-2 rounded-xl border p-4 text-center transition-colors',
                  isSelected
                    ? 'border-accent ring-2 ring-accent'
                    : 'border-border hover:bg-muted',
                )}
              >
                <ProviderIcon provider={recipe.displayName} className="h-10 w-10 text-xl" />
                <span className="text-sm font-medium">{recipe.displayName}</span>
              </button>
            )
          })}
        </div>
      )}

      <p className="text-xs text-muted-foreground">
        Don't see it? Ask your operator to add a recipe — the portal never authors one.
      </p>
    </div>
  )
}

function PersonStep({
  isAdmin,
  people,
  currentPrincipal,
  principal,
  isSelf,
  onPrincipal,
}: {
  isAdmin: boolean
  people?: Person[]
  currentPrincipal: string
  principal: string
  isSelf: boolean
  onPrincipal: (value: string) => void
}) {
  const quickPicks = (people ?? []).map((person) => person.principal)
  return (
    <div className="flex flex-col gap-4">
      <div className="space-y-1">
        <h2 className="text-base font-medium">Who is this account for?</h2>
        <p className="text-sm text-muted-foreground">
          This is who the agent will act as. It does not share a password.
        </p>
      </div>

      {isAdmin && quickPicks.length > 0 ? (
        <div className="flex flex-wrap gap-2">
          {quickPicks.map((candidate) => (
            <Button
              key={candidate}
              type="button"
              size="sm"
              variant={candidate === principal ? 'default' : 'outline'}
              onClick={() => onPrincipal(candidate)}
            >
              {candidate}
              {candidate === currentPrincipal ? ' (you)' : ''}
            </Button>
          ))}
        </div>
      ) : null}

      <div className="flex flex-col gap-1.5">
        <Label htmlFor="connect-principal">
          Person {isSelf ? <span className="text-muted-foreground">(you)</span> : null}
        </Label>
        <Input
          id="connect-principal"
          type="email"
          inputMode="email"
          autoComplete="off"
          value={principal}
          disabled={!isAdmin}
          onChange={(event) => onPrincipal(event.target.value)}
          placeholder="bob@example.com"
        />
        <p className="text-xs text-muted-foreground">
          {isAdmin
            ? 'As an admin you can connect an account for another person (e.g. a family member).'
            : 'Members can only connect their own accounts.'}
        </p>
      </div>
    </div>
  )
}

function CredentialStep({
  value,
  suggestion,
  onChange,
}: {
  value: string
  suggestion: string
  onChange: (value: string) => void
}) {
  return (
    <div className="flex flex-col gap-4">
      <div className="space-y-1">
        <h2 className="text-base font-medium">Name the stored credential</h2>
        <p className="text-sm text-muted-foreground">
          This is the name of the secret in your vault that holds (or will hold) the session
          bundle — never a password.
        </p>
      </div>

      <div className="flex flex-col gap-1.5">
        <Label htmlFor="connect-credential">Stored credential name</Label>
        <div className="flex items-center gap-2">
          <KeyRound className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
          <Input
            id="connect-credential"
            type="text"
            autoComplete="off"
            value={value}
            onChange={(event) => onChange(event.target.value)}
            placeholder={suggestion || 'health-portal-bob-session'}
          />
        </div>
        {suggestion && value !== suggestion ? (
          <button
            type="button"
            onClick={() => onChange(suggestion)}
            className="self-start text-xs font-medium text-accent hover:underline"
          >
            Use “{suggestion}”
          </button>
        ) : null}
      </div>

      <Alert className="text-left">
        <AlertDescription className="flex items-start gap-2 text-muted-foreground">
          <Info className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
          <span>
            Tessera stores the session in your vault under this name and only ever learns that it's
            present. <span className="text-foreground">Tessera can't show this — that's the point.</span>
          </span>
        </AlertDescription>
      </Alert>
    </div>
  )
}

function SeedStep({
  displayName,
  seed,
  onSeed,
  onOpenHandoff,
}: {
  displayName: string
  seed: SeedState
  onSeed: () => void
  onOpenHandoff?: () => void
}) {
  const label = displayName || 'this account'
  return (
    <div className="flex flex-col gap-4">
      <div className="space-y-1">
        <h2 className="text-base font-medium">Seed the session</h2>
        <p className="text-sm text-muted-foreground">
          Seeding holds a logged-in browser session for {label}. We keep the session — never your
          password.
        </p>
      </div>

      {seed.phase === 'ready' && seed.handle ? (
        <Alert className="text-left">
          <AlertTitle>Live hand-off is ready</AlertTitle>
          <AlertDescription className="flex flex-col gap-3">
            <span>A short-lived hand-off was started. Open it to finish logging in.</span>
            {onOpenHandoff ? (
              <Button size="sm" variant="outline" className="self-start" onClick={onOpenHandoff}>
                Open live hand-off
                <ArrowRight className="h-4 w-4" />
              </Button>
            ) : null}
          </AlertDescription>
        </Alert>
      ) : seed.phase === 'unavailable' ? (
        <Alert className="text-left">
          <AlertTitle>Live hand-off isn't configured yet</AlertTitle>
          <AlertDescription className="flex flex-col gap-2 text-muted-foreground">
            <span>{seed.reason}</span>
            <span>
              Seeding is done with the documented worker for now. You can skip this and seed later —
              the connection will simply show as <span className="text-foreground">Absent</span>{' '}
              until it's seeded.
            </span>
            <Button size="sm" variant="outline" className="self-start" onClick={onSeed}>
              <RotateCw className="h-4 w-4" />
              Try again
            </Button>
          </AlertDescription>
        </Alert>
      ) : (
        <div>
          <Button onClick={onSeed} disabled={seed.phase === 'loading'}>
            <RotateCw className={cn('h-4 w-4', seed.phase === 'loading' && 'animate-spin')} />
            {seed.phase === 'loading' ? 'Starting…' : 'Seed now'}
          </Button>
        </div>
      )}
    </div>
  )
}

function FinishStep({
  displayName,
  principal,
  isSelf,
  credential,
  seeded,
  submitError,
}: {
  displayName: string
  principal: string
  isSelf: boolean
  credential: string
  seeded: boolean
  submitError: string | null
}) {
  return (
    <div className="flex flex-col gap-4">
      <div className="space-y-1">
        <h2 className="text-base font-medium">Review &amp; connect</h2>
        <p className="text-sm text-muted-foreground">
          Tessera will create the binding. It won't be live until a session is seeded.
        </p>
      </div>

      <dl className="divide-y divide-border rounded-xl border border-border">
        <SummaryRow term="Provider" detail={displayName || '—'} />
        <SummaryRow term="Person" detail={`${principal}${isSelf ? ' (you)' : ''}`} />
        <SummaryRow term="Stored credential" detail={credential || '—'} />
        <SummaryRow
          term="Status after connect"
          detail={seeded ? 'Seeding started — verifying' : 'Absent — seed a session to go live'}
        />
      </dl>

      {submitError ? (
        <Alert variant="destructive" className="text-left">
          <AlertDescription className="text-foreground">{submitError}</AlertDescription>
        </Alert>
      ) : null}
    </div>
  )
}

function SummaryRow({ term, detail }: { term: string; detail: string }) {
  return (
    <div className="flex items-center justify-between gap-4 px-4 py-3">
      <dt className="text-sm text-muted-foreground">{term}</dt>
      <dd className="truncate text-sm font-medium">{detail}</dd>
    </div>
  )
}
