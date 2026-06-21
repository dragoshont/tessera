import {
  useApprovePendingWrite,
  useDenyPendingWrite,
  usePendingWrites,
} from '../api/hooks'
import { HttpError } from '../api/client'
import { PendingWritesTable } from '../components/pending/PendingWritesTable'

/** A friendly, non-blocking reason a decision didn't land — the list refetches regardless. */
function decideErrorMessage(error: unknown): string {
  if (error instanceof HttpError && error.status === 404) {
    return 'That write is no longer waiting — it may have expired or already been decided. The list was refreshed.'
  }
  return 'Your decision could not be recorded. The list was refreshed — please try again.'
}

/**
 * "Pending writes" — the out-of-band approval surface (ADR 0023). The broker holds
 * a write an agent asked to make *as you* and forwards nothing until you decide it
 * here, signed in with your own token (a channel the calling agent cannot forge).
 *
 * Honest by construction: approving only *authorizes the original caller to re-issue
 * the exact request* — Tessera does not perform the write from this page, and this
 * page never claims one succeeded. Self-scoped server-side: you only ever see and
 * decide your own held writes, and only a human summary + a body excerpt, never a
 * secret value.
 */
export function PendingWritesPage() {
  const { data: writes, isLoading } = usePendingWrites()
  const approve = useApprovePendingWrite()
  const deny = useDenyPendingWrite()

  // The id of the write currently being decided (either action) — drives the row's
  // busy/disabled state so a decision can't be double-submitted.
  const decidingId =
    (approve.isPending ? approve.variables : undefined) ??
    (deny.isPending ? deny.variables : undefined) ??
    null

  const lastError = approve.error ?? deny.error
  const errorMessage = lastError ? decideErrorMessage(lastError) : null

  return (
    <div className="mx-auto max-w-3xl space-y-8">
      <header className="space-y-1">
        <h1 className="text-xl font-semibold">Pending writes</h1>
        <p className="text-sm text-muted-foreground">
          Changes an agent has asked to make as you, held here until you decide. Approving authorizes
          the requester to complete that exact change — Tessera does not make the change from this
          page. Deny to refuse it.
        </p>
      </header>

      <PendingWritesTable
        items={writes}
        isLoading={isLoading}
        decidingId={decidingId}
        errorMessage={errorMessage}
        onApprove={(id) => approve.mutate(id)}
        onDeny={(id) => deny.mutate(id)}
        emptyHint="No writes are waiting for your approval."
      />
    </div>
  )
}
