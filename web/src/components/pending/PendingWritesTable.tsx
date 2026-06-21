import { Check, Loader2, X } from 'lucide-react'
import { Alert, AlertDescription } from '../ui/alert'
import { Badge } from '../ui/badge'
import { Button } from '../ui/button'
import { Card, CardContent } from '../ui/card'
import { Skeleton } from '../ui/skeleton'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '../ui/table'
import { cn } from '../../lib/utils'
import type { PendingWrite, PendingWriteStatus } from '../../data/types'

// The held-write approval list (ADR 0023). Presentational only — the page wires
// the query + the approve/deny mutations and passes handlers down; this never
// fetches and never shows a secret value (a human summary + a body EXCERPT only).

const STATUS_STYLE: Record<PendingWriteStatus, { label: string; className: string }> = {
  // Amber = waiting on you (a to-do, not a failure); green = decided yes; red =
  // refused; muted = lapsed. Mirrors the Activity feed's effect colouring.
  pending: { label: 'pending', className: 'text-health-expiring' },
  approved: { label: 'approved', className: 'text-health-live' },
  consumed: { label: 'completed', className: 'text-health-live' },
  denied: { label: 'denied', className: 'text-health-error' },
  expired: { label: 'expired', className: 'text-muted-foreground' },
}

/** "expires in N min" / "expires in <1 min" / "expired" — computed, no fake precision. */
function expiresLabel(iso: string): string {
  const ms = new Date(iso).getTime() - Date.now()
  if (Number.isNaN(ms)) return iso
  if (ms <= 0) return 'expired'
  const minutes = Math.round(ms / 60_000)
  return minutes < 1 ? 'expires in <1 min' : `expires in ${minutes} min`
}

/** Collapse a multi-line body (e.g. a VEVENT) to one line for the inline excerpt. */
function oneLine(text: string): string {
  return text.replace(/\s+/g, ' ').trim()
}

export interface PendingWritesTableProps {
  /** The writes still waiting for the signed-in person's decision. */
  items?: PendingWrite[]
  isLoading?: boolean
  /** The id currently being approved/denied — its row's buttons show busy + disable. */
  decidingId?: string | null
  /** A non-blocking message from the last failed decision (e.g. it expired). */
  errorMessage?: string | null
  /** Approve a held write (authorizes the caller to re-issue it; does not perform it). */
  onApprove?: (id: string) => void
  /** Deny a held write (it will never be forwarded). */
  onDeny?: (id: string) => void
  /** Shown when nothing is waiting. */
  emptyHint?: string
}

function PendingWriteRow({
  write,
  busy,
  onApprove,
  onDeny,
}: {
  write: PendingWrite
  busy: boolean
  onApprove?: (id: string) => void
  onDeny?: (id: string) => void
}) {
  const status = STATUS_STYLE[write.status]
  const expires = expiresLabel(write.expiresAt)
  const resource = `${write.method} ${write.upstreamHost}${write.pathAndQuery}`
  return (
    <TableRow>
      <TableCell className="align-top">
        <div className="min-w-0 max-w-sm space-y-1.5">
          <p className="font-medium text-foreground">{write.summary}</p>
          <p className="truncate font-mono text-xs text-muted-foreground" title={resource}>
            <span className="font-semibold uppercase text-foreground">{write.method}</span>{' '}
            {write.upstreamHost}
            {write.pathAndQuery}
          </p>
          {write.bodyExcerpt ? (
            <code
              className="block truncate rounded bg-muted/60 px-1.5 py-1 font-mono text-xs text-muted-foreground"
              title={write.bodyExcerpt}
            >
              {oneLine(write.bodyExcerpt)}
            </code>
          ) : null}
        </div>
      </TableCell>
      <TableCell className="align-top">
        <Badge variant="outline" className={cn('shrink-0', status.className)}>
          {status.label}
        </Badge>
      </TableCell>
      <TableCell className="align-top">
        <span
          className={cn(
            'whitespace-nowrap text-xs',
            expires === 'expired' ? 'text-health-error' : 'text-health-expiring',
          )}
        >
          {expires}
        </span>
      </TableCell>
      <TableCell className="align-top">
        <div className="flex flex-col gap-2">
          <Button
            size="sm"
            variant="default"
            disabled={busy}
            onClick={() => onApprove?.(write.id)}
            aria-label={`Approve: ${write.summary}`}
          >
            {busy ? (
              <Loader2 className="h-4 w-4 animate-spin" aria-hidden />
            ) : (
              <Check className="h-4 w-4" aria-hidden />
            )}
            Approve
          </Button>
          <Button
            size="sm"
            variant="outline"
            disabled={busy}
            onClick={() => onDeny?.(write.id)}
            aria-label={`Deny: ${write.summary}`}
          >
            <X className="h-4 w-4" aria-hidden />
            Deny
          </Button>
        </div>
      </TableCell>
    </TableRow>
  )
}

export function PendingWritesTable({
  items,
  isLoading,
  decidingId,
  errorMessage,
  onApprove,
  onDeny,
  emptyHint,
}: PendingWritesTableProps) {
  if (isLoading) {
    return (
      <div className="space-y-3">
        <Skeleton className="h-12 w-full" />
        <Skeleton className="h-12 w-full" />
        <Skeleton className="h-12 w-full" />
      </div>
    )
  }

  const writes = items ?? []

  return (
    <div className="space-y-4">
      {errorMessage ? (
        <Alert variant="warning">
          <AlertDescription>{errorMessage}</AlertDescription>
        </Alert>
      ) : null}

      {writes.length === 0 ? (
        <p className="rounded-lg border border-dashed border-border px-4 py-8 text-center text-sm text-muted-foreground">
          {emptyHint ?? 'No writes are waiting for your approval.'}
        </p>
      ) : (
        <Card>
          <CardContent className="p-0">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Change</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead>Expires</TableHead>
                  <TableHead className="sr-only">Decision</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {writes.map((write) => (
                  <PendingWriteRow
                    key={write.id}
                    write={write}
                    busy={decidingId === write.id}
                    onApprove={onApprove}
                    onDeny={onDeny}
                  />
                ))}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
      )}
    </div>
  )
}
