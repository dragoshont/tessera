import { formatDistanceToNow } from 'date-fns'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '../ui/tabs'
import { Badge } from '../ui/badge'
import { Skeleton } from '../ui/skeleton'
import { cn } from '../../lib/utils'
import type { AuditEffect, AuditFeed, AuditRow } from '../../data/types'

// The secret-free activity feed (ADR 0017). Two ways to read the same scoped
// window: a calm human Timeline and a terminal-style Log. Presentational only —
// the page wires the data + scope; this never fetches and never shows a secret.

const EFFECT_STYLE: Record<AuditEffect, { label: string; className: string }> = {
  allow: { label: 'allow', className: 'text-health-live' },
  deny: { label: 'deny', className: 'text-health-error' },
  'step-up': { label: 'step-up', className: 'text-health-expiring' },
}

function relativeTime(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return iso
  return formatDistanceToNow(date, { addSuffix: true })
}

/** The actor phrase: "<caller> acting as <person>" or "<caller> (automation)". */
function actorPhrase(row: AuditRow): string {
  const caller = shortCaller(row.caller)
  return row.onBehalfOf ? `${caller} · as ${row.onBehalfOf}` : `${caller} · automation`
}

/** Trims a SPIFFE/MCP id to its last path segment for compact display. */
function shortCaller(caller: string): string {
  const slash = caller.lastIndexOf('/')
  return slash >= 0 ? caller.slice(slash + 1) : caller
}

function StatPill({ label, value, className }: { label: string; value: number; className?: string }) {
  return (
    <div className="flex flex-col rounded-lg border border-border bg-card px-3 py-2">
      <span className={cn('text-lg font-semibold tabular-nums', className)}>{value}</span>
      <span className="text-xs text-muted-foreground">{label}</span>
    </div>
  )
}

function SummaryStrip({ summary }: { summary: AuditFeed['summary'] }) {
  return (
    <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
      <StatPill label="Total" value={summary.total} />
      <StatPill label="Allowed" value={summary.allow} className="text-health-live" />
      <StatPill label="Step-up" value={summary.stepUp} className="text-health-expiring" />
      <StatPill label="Denied" value={summary.deny} className="text-health-error" />
    </div>
  )
}

function TimelineRow({ row }: { row: AuditRow }) {
  const effect = EFFECT_STYLE[row.effect]
  return (
    <li className="flex items-start gap-3 border-b border-border/60 py-2.5 last:border-0">
      <Badge variant="outline" className={cn('mt-0.5 shrink-0', effect.className)}>
        {effect.label}
      </Badge>
      <div className="min-w-0 flex-1">
        <p className="truncate text-sm font-medium">
          {row.action} <span className="text-muted-foreground">on</span> {row.target}
        </p>
        <p className="truncate text-xs text-muted-foreground">{actorPhrase(row)}</p>
      </div>
      <time className="shrink-0 text-xs text-muted-foreground" dateTime={row.timestamp}>
        {relativeTime(row.timestamp)}
      </time>
    </li>
  )
}

function LogLine({ row }: { row: AuditRow }) {
  const effect = EFFECT_STYLE[row.effect]
  return (
    <div className="flex gap-2 whitespace-pre-wrap break-all py-0.5">
      <span className="shrink-0 text-muted-foreground">{new Date(row.timestamp).toISOString()}</span>
      <span className={cn('shrink-0 font-semibold uppercase', effect.className)}>{row.effect}</span>
      <span>
        {shortCaller(row.caller)} {row.onBehalfOf ? `as ${row.onBehalfOf}` : '(automation)'} →{' '}
        {row.target} {row.action}
      </span>
    </div>
  )
}

export interface ActivityFeedProps {
  feed?: AuditFeed
  isLoading?: boolean
  /** Shown when the scoped window has no entries. */
  emptyHint?: string
}

export function ActivityFeed({ feed, isLoading, emptyHint }: ActivityFeedProps) {
  if (isLoading) {
    return (
      <div className="space-y-3">
        <Skeleton className="h-16 w-full" />
        <Skeleton className="h-40 w-full" />
      </div>
    )
  }

  const entries = feed?.entries ?? []
  const summary = feed?.summary

  return (
    <div className="space-y-4">
      {summary ? <SummaryStrip summary={summary} /> : null}
      {entries.length === 0 ? (
        <p className="rounded-lg border border-dashed border-border px-4 py-8 text-center text-sm text-muted-foreground">
          {emptyHint ?? 'No activity yet. Brokering decisions will appear here as they happen.'}
        </p>
      ) : (
        <Tabs defaultValue="timeline">
          <TabsList>
            <TabsTrigger value="timeline">Timeline</TabsTrigger>
            <TabsTrigger value="log">Log</TabsTrigger>
          </TabsList>
          <TabsContent value="timeline">
            <ul className="rounded-lg border border-border bg-card px-4">
              {entries.map((row, index) => (
                <TimelineRow key={`${row.timestamp}-${index}`} row={row} />
              ))}
            </ul>
          </TabsContent>
          <TabsContent value="log">
            <div className="max-h-96 overflow-auto rounded-lg border border-border bg-muted/40 p-3 font-mono text-xs leading-relaxed">
              {entries.map((row, index) => (
                <LogLine key={`${row.timestamp}-${index}`} row={row} />
              ))}
            </div>
          </TabsContent>
        </Tabs>
      )}
    </div>
  )
}
