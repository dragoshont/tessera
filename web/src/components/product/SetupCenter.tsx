import type { R2SetupStatus } from '../../api/r2'
import { recoveryMessage } from '../../lib/product-error'
import { Alert, AlertDescription } from '../ui/alert'
import { Button } from '../ui/button'
import { ProductStateBadge } from './R2ProductComponents'

export function SetupCenter({ status, busy, error, onRetry, onAccounts }: {
  status: R2SetupStatus
  busy: boolean
  error: Error | null
  onRetry: () => void
  onAccounts: () => void
}) {
  return (
    <section className="mx-auto max-w-3xl py-10" aria-labelledby="setup-title">
      <h1 id="setup-title" className="text-2xl font-semibold">Welcome to Tessera</h1>
      <p className="mt-2 text-sm text-muted-foreground">Tessera checks what is already available and only asks for missing account authorization.</p>
      <div className="mt-6 divide-y divide-border border-y border-border">
        <SetupRow name={status.server.displayName || 'Tessera Home'} detail={`Version ${status.server.version}`} state={status.server.state} />
        <SetupRow name="AI" detail={status.ai.model ?? status.ai.displayName ?? 'No model available'} state={busy ? 'CONNECTING' : status.ai.state} />
        {status.integrations.map((integration) => <SetupRow
          key={integration.id}
          name={integration.name}
          detail={integration.state === 'CONNECTED'
            ? 'Account connected'
            : integration.runtimeState === 'READY'
              ? 'Ready for account authorization'
              : integration.detailCode?.replaceAll('_', ' ') ?? 'Unavailable'}
          state={integration.state}
        />)}
      </div>
      {error ? <Alert variant="destructive" className="mt-5"><AlertDescription>{recoveryMessage(null, error.message)}</AlertDescription></Alert> : null}
      <div className="mt-6 flex flex-wrap gap-3">
        {status.ai.state !== 'CONNECTED' ? <Button onClick={onRetry} disabled={busy}>{busy ? 'Connecting AI…' : 'Retry AI connection'}</Button> : null}
        <Button variant="outline" onClick={onAccounts}>Review accounts</Button>
      </div>
    </section>
  )
}

function SetupRow({ name, detail, state }: { name: string; detail: string; state: string }) {
  return <div className="flex min-h-16 items-center justify-between gap-4 py-3"><div><p className="font-medium">{name}</p><p className="text-sm text-muted-foreground">{detail}</p></div><ProductStateBadge state={state} /></div>
}