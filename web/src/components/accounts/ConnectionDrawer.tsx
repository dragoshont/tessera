import { RotateCw, Trash2 } from 'lucide-react'
import type { Connection, Schedule } from '../../data/types'
import { formatExpiry, relativeTime } from '../../lib/format'
import { Alert, AlertDescription } from '../ui/alert'
import { Button } from '../ui/button'
import { Separator } from '../ui/separator'
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetFooter,
  SheetHeader,
  SheetTitle,
} from '../ui/sheet'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '../ui/tabs'
import { HealthBadge } from '../badges/HealthBadge'
import { ProviderIcon } from '../common/ProviderIcon'
import { PresenceFlags } from './PresenceFlags'

export type ConnectionDrawerAction = 'reseed' | 'seed' | 'revoke'

export interface ConnectionDrawerProps {
  connection: Connection | null
  open: boolean
  onOpenChange: (open: boolean) => void
  onAction?: (action: ConnectionDrawerAction, connection: Connection) => void
  /** The connection's rotation schedule (ADR 0017) — shown as the auto-refresh row when present. */
  schedule?: Schedule
}

/** Phrases the rotation owner for the auto-refresh detail row (honest about who rotates). */
function rotationLabel(schedule: Schedule): string {
  switch (schedule.rotationOwner) {
    case 'tessera':
      return 'Automatic · Tessera'
    case 'external':
      return 'Automatic · external'
    default:
      return 'Manual · re-seed by hand'
  }
}

function DetailRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-baseline justify-between gap-4 py-1.5 text-sm">
      <span className="text-muted-foreground">{label}</span>
      <span className="text-right font-medium">{value}</span>
    </div>
  )
}

function ActivityTimeline({ connection }: { connection: Connection }) {
  const events = [
    connection.lastUsedAt
      ? { at: connection.lastUsedAt, text: 'used by agent · allowed' }
      : null,
    connection.lastSeededAt
      ? { at: connection.lastSeededAt, text: 're-seeded · human' }
      : null,
  ]
    .filter((event): event is { at: string; text: string } => event !== null)
    .sort((a, b) => new Date(b.at).getTime() - new Date(a.at).getTime())

  if (events.length === 0) {
    return <p className="text-sm text-muted-foreground">No activity yet.</p>
  }

  return (
    <div className="flex flex-col gap-3">
      <p className="text-xs uppercase tracking-wide text-muted-foreground">Activity (secret-free)</p>
      <ul className="flex flex-col gap-3">
        {events.map((event, index) => (
          <li key={index} className="flex items-start gap-3 text-sm">
            <span className="mt-1.5 h-1.5 w-1.5 shrink-0 rounded-full bg-muted-foreground" aria-hidden />
            <span>
              <span className="text-muted-foreground">{relativeTime(event.at)}</span> — {event.text}
            </span>
          </li>
        ))}
      </ul>
    </div>
  )
}

export function ConnectionDrawer({ connection, open, onOpenChange, onAction, schedule }: ConnectionDrawerProps) {
  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      {connection ? (
        <SheetContent side="right" className="w-full sm:max-w-lg">
          <SheetHeader>
            <div className="flex items-center gap-3">
              <ProviderIcon provider={connection.provider} />
              <div className="min-w-0">
                <SheetTitle className="truncate">{connection.displayName}</SheetTitle>
                <SheetDescription className="truncate">
                  acting as {connection.ownerPrincipal}
                </SheetDescription>
              </div>
            </div>
            <div className="flex items-center gap-2 pt-1 text-sm">
              <HealthBadge status={connection.status} />
              {connection.lastUsedAt || connection.lastSeededAt ? (
                <span className="text-muted-foreground">
                  · verified {relativeTime(connection.lastUsedAt ?? connection.lastSeededAt)}
                </span>
              ) : null}
            </div>
          </SheetHeader>

          <div className="flex-1 overflow-y-auto p-6">
            <Tabs defaultValue="overview" className="flex flex-col gap-6">
              <TabsList>
                <TabsTrigger value="overview">Overview</TabsTrigger>
                <TabsTrigger value="activity">Activity</TabsTrigger>
                <TabsTrigger value="policy">Policy</TabsTrigger>
              </TabsList>

              <TabsContent value="overview" className="flex flex-col gap-6">
                {connection.status === 'error' ? (
                  <Alert variant="destructive">
                    <AlertDescription className="text-foreground">
                      Last check failed. Re-seed this connection to restore it.
                    </AlertDescription>
                  </Alert>
                ) : null}

                <section className="flex flex-col gap-3">
                  <h4 className="text-sm font-semibold">Session contents</h4>
                  <PresenceFlags connection={connection} />
                </section>

                <Separator />

                <section className="flex flex-col">
                  <h4 className="pb-1 text-sm font-semibold">Health</h4>
                  <DetailRow
                    label="last re-seeded"
                    value={connection.lastSeededAt ? relativeTime(connection.lastSeededAt) : '—'}
                  />
                  <DetailRow
                    label="last used"
                    value={connection.lastUsedAt ? relativeTime(connection.lastUsedAt) : '—'}
                  />
                  <DetailRow label="expires" value={formatExpiry(connection)} />
                  {schedule ? <DetailRow label="auto-refresh" value={rotationLabel(schedule)} /> : null}
                  <DetailRow label="acts as" value={connection.ownerPrincipal} />
                </section>
              </TabsContent>

              <TabsContent value="activity">
                <ActivityTimeline connection={connection} />
              </TabsContent>

              <TabsContent value="policy" className="flex flex-col gap-3">
                <p className="text-sm">
                  This connection acts as{' '}
                  <span className="font-medium">{connection.ownerPrincipal}</span>.
                </p>
                <p className="text-sm text-muted-foreground">
                  Policy — who and what may use it — is file-backed and reviewed in Git. Tessera reads
                  it here; it isn't edited in the portal.
                </p>
              </TabsContent>
            </Tabs>
          </div>

          <SheetFooter>
            {connection.status === 'absent' ? (
              <Button className="flex-1" onClick={() => onAction?.('seed', connection)}>
                <RotateCw className="h-4 w-4" aria-hidden />
                Seed now
              </Button>
            ) : (
              <Button className="flex-1" onClick={() => onAction?.('reseed', connection)}>
                <RotateCw className="h-4 w-4" aria-hidden />
                Re-seed
              </Button>
            )}
            <Button
              variant="ghost"
              className="text-health-error hover:bg-health-error/10"
              onClick={() => onAction?.('revoke', connection)}
            >
              <Trash2 className="h-4 w-4" aria-hidden />
              {connection.status === 'absent' ? 'Remove' : 'Revoke…'}
            </Button>
          </SheetFooter>
        </SheetContent>
      ) : null}
    </Sheet>
  )
}
