import { Plus, Workflow } from 'lucide-react'
import { Button } from '../ui/button'

export function AccountsEmptyState({ onConnectAccount }: { onConnectAccount?: () => void }) {
  return (
    <div className="flex flex-col items-center gap-4 rounded-xl border border-dashed border-border bg-card px-6 py-16 text-center">
      <span className="flex h-12 w-12 items-center justify-center rounded-2xl bg-muted text-muted-foreground">
        <Workflow className="h-6 w-6" aria-hidden />
      </span>
      <div className="space-y-1">
        <h3 className="text-base font-semibold">Connect your first account</h3>
        <p className="mx-auto max-w-md text-sm text-muted-foreground">
          Tessera holds a logged-in session so an agent can act for you — without ever seeing your
          password.
        </p>
      </div>
      <Button onClick={onConnectAccount}>
        <Plus className="h-4 w-4" aria-hidden />
        Connect account
      </Button>
    </div>
  )
}
