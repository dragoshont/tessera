import { useRef, useState } from 'react'
import {
  Check,
  CircleCheck,
  Clock,
  FileInput,
  History,
  Loader2,
  Pencil,
  Search,
  Sparkles,
  TriangleAlert,
} from 'lucide-react'
import type {
  FollowUpDetail,
  FollowUpField,
  FollowUpRevision,
  FollowUpStatus,
  FollowUpSummary,
  FollowUpWhy,
} from '../../data/types'
import { cn } from '../../lib/utils'
import { Alert, AlertDescription } from '../ui/alert'
import { Badge } from '../ui/badge'
import { Button } from '../ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '../ui/dialog'
import { Input } from '../ui/input'
import { Label } from '../ui/label'
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
} from '../ui/sheet'
import { Skeleton } from '../ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '../ui/tabs'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../ui/table'

export type ContinuityView = 'attention' | 'tracked'

const FIELD_LABEL: Record<FollowUpField, string> = {
  deliverable: 'Deliverable',
  counterparty: 'Counterparty',
  dueAt: 'Due date',
  completedAt: 'Completed',
}

const STATUS_META: Record<
  FollowUpStatus,
  { label: string; className: string; icon: typeof Clock }
> = {
  attention: { label: 'Candidate', className: 'text-health-expiring', icon: Clock },
  conflict: { label: 'Conflict', className: 'text-accent', icon: TriangleAlert },
  tracked: { label: 'Current', className: 'text-health-live', icon: CircleCheck },
  completed: { label: 'Completed', className: 'text-health-live', icon: Check },
}

function StatusBadge({ status }: { status: FollowUpStatus }) {
  const meta = STATUS_META[status]
  const Icon = meta.icon
  return (
    <Badge
      variant="outline"
      className={cn('gap-1.5', meta.className)}
      data-continuity-state={status}
    >
      <Icon className="h-3.5 w-3.5" aria-hidden />
      {meta.label}
    </Badge>
  )
}

function displayValue(value: string | null): string {
  return value ?? 'Awaiting acceptance'
}

function FollowUpTable({
  items,
  onSelect,
}: {
  items: FollowUpSummary[]
  onSelect?: (id: string) => void
}) {
  return (
    <>
      <div className="hidden overflow-hidden rounded-lg border border-border bg-card md:block">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead scope="col">Follow-up</TableHead>
              <TableHead scope="col">With</TableHead>
              <TableHead scope="col">Due</TableHead>
              <TableHead scope="col">State</TableHead>
              <TableHead className="sr-only" scope="col">Open</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {items.map((item) => (
              <TableRow key={item.followUpId}>
                <TableCell className="font-medium">{displayValue(item.deliverable)}</TableCell>
                <TableCell>{displayValue(item.counterparty)}</TableCell>
                <TableCell>{displayValue(item.dueAt)}</TableCell>
                <TableCell><StatusBadge status={item.status} /></TableCell>
                <TableCell className="text-right">
                  <Button
                    size="sm"
                    variant="ghost"
                    onClick={() => onSelect?.(item.followUpId)}
                    aria-label={`Open ${displayValue(item.deliverable)}`}
                  >
                    <Search className="h-4 w-4" aria-hidden />
                    Detail
                  </Button>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>

      <ul className="space-y-2 md:hidden" aria-label="Follow-ups">
        {items.map((item) => (
          <li key={item.followUpId} className="rounded-lg border border-border bg-card p-4">
            <div className="flex items-start justify-between gap-3">
              <div className="min-w-0">
                <p className="font-medium">{displayValue(item.deliverable)}</p>
                <dl className="mt-2 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm">
                  <dt className="text-muted-foreground">With</dt>
                  <dd>{displayValue(item.counterparty)}</dd>
                  <dt className="text-muted-foreground">Due</dt>
                  <dd>{displayValue(item.dueAt)}</dd>
                </dl>
              </div>
              <StatusBadge status={item.status} />
            </div>
            <Button
              className="mt-3 w-full"
              size="sm"
              variant="outline"
              onClick={() => onSelect?.(item.followUpId)}
            >
              <Search className="h-4 w-4" aria-hidden />
              Open detail
            </Button>
          </li>
        ))}
      </ul>
    </>
  )
}

function RevisionStateBadge({ revision }: { revision: FollowUpRevision }) {
  const state = revision.state
  const className = state === 'current'
    ? 'text-health-live'
    : state === 'candidate'
      ? 'text-health-expiring'
      : state === 'conflicted'
        ? 'text-accent'
        : 'text-muted-foreground'
  const Icon = state === 'current'
    ? CircleCheck
    : state === 'candidate'
      ? Clock
      : state === 'conflicted'
        ? TriangleAlert
        : History
  return <Badge variant="outline" className={cn('gap-1.5', className)}><Icon className="h-3.5 w-3.5" aria-hidden />{state}</Badge>
}

function Overview({ detail }: { detail: FollowUpDetail }) {
  const visible = detail.revisions.filter((revision) =>
    revision.state === 'current' || revision.state === 'candidate' || revision.state === 'conflicted')
  return (
    <dl className="divide-y divide-border rounded-lg border border-border">
      {visible.map((revision) => (
        <div key={revision.revisionId} className="grid gap-2 px-4 py-3 sm:grid-cols-[8rem_1fr_auto] sm:items-center">
          <dt className="text-sm text-muted-foreground">{FIELD_LABEL[revision.field]}</dt>
          <dd className="text-sm font-medium">{revision.value}</dd>
          <dd><RevisionStateBadge revision={revision} /></dd>
        </div>
      ))}
    </dl>
  )
}

function Timeline({ detail }: { detail: FollowUpDetail }) {
  return (
    <div>
      {detail.timelineTruncated ? (
        <Alert variant="warning" className="mb-4"><AlertDescription>Showing the latest 100 timeline entries.</AlertDescription></Alert>
      ) : null}
      <ol className="space-y-0" aria-label="Follow-up timeline">
      {[...detail.timeline].reverse().map((entry) => (
        <li key={entry.sequence} className="grid grid-cols-[1rem_1fr] gap-3 border-b border-border py-3 last:border-0">
          <span className="mt-1.5 h-2 w-2 rounded-full bg-muted-foreground" aria-hidden />
          <div>
            <p className="text-sm font-medium">{entry.summary}</p>
            <p className="mt-1 text-xs text-muted-foreground">
              Source {new Date(entry.sourceTimestamp).toLocaleString()} · recorded{' '}
              {new Date(entry.recordedAt).toLocaleString()}
            </p>
          </div>
        </li>
      ))}
      </ol>
    </div>
  )
}

function Why({
  why,
  loading,
  failed,
}: {
  why?: FollowUpWhy | null
  loading?: boolean
  failed?: boolean
}) {
  if (loading) {
    return <Alert><AlertDescription>Loading source lineage…</AlertDescription></Alert>
  }
  if (failed || !why) {
    return (
      <Alert variant="destructive">
        <AlertDescription>Source lineage is unavailable. No provenance is shown until Why loads successfully.</AlertDescription>
      </Alert>
    )
  }
  const consequential = Object.values(why.fields).flatMap((revisions) => revisions ?? [])
  return (
    <div className="space-y-4">
      {why?.truncated ? (
        <Alert variant="warning"><AlertDescription>Showing the latest 100 provenance revisions.</AlertDescription></Alert>
      ) : null}
      {consequential.map((revision) => (
        <section
          key={revision.revisionId}
          className="rounded-lg border border-border p-4"
          data-revision-id={revision.revisionId}
          data-follow-up-field={revision.field}
        >
          <div className="flex items-start justify-between gap-3">
            <div>
              <h3 className="text-sm font-semibold">{FIELD_LABEL[revision.field]}</h3>
              <p className="mt-1 text-sm">{revision.value}</p>
            </div>
            <RevisionStateBadge revision={revision} />
          </div>
          <dl className="mt-3 grid gap-2 text-xs sm:grid-cols-2">
            <div><dt className="text-muted-foreground">Source time</dt><dd>{new Date(revision.sourceTimestamp).toLocaleString()}</dd></div>
            <div><dt className="text-muted-foreground">Confidence</dt><dd>{Math.round(revision.confidence * 100)}%</dd></div>
            <div><dt className="text-muted-foreground">Parser</dt><dd className="font-mono">{revision.parserVersion}</dd></div>
            <div className="sm:col-span-2">
              <dt className="text-muted-foreground">Evidence</dt>
              <dd>
                <ul className="mt-1 space-y-1 font-mono">
                  {revision.evidenceRefs.map((reference) => (
                    <li key={reference} className="break-all">{reference}</li>
                  ))}
                </ul>
              </dd>
            </div>
          </dl>
          {revision.correctionEvidenceRef ? (
            <p className="mt-3 text-xs text-muted-foreground">Current through explicit user correction.</p>
          ) : null}
          {revision.lineageRevisionRefs.length > 0 ? (
            <div className="mt-3 text-xs text-muted-foreground">
              <p>Prior accepted context</p>
              <ul className="mt-1 space-y-1 font-mono">
                {revision.lineageRevisionRefs.map((reference) => (
                  <li key={reference} className="break-all">{reference}</li>
                ))}
              </ul>
            </div>
          ) : null}
        </section>
      ))}
    </div>
  )
}

function DecisionDialog({
  detail,
  mode,
  onOpenChange,
  onSubmit,
  busy,
  returnFocus,
}: {
  detail: FollowUpDetail
  mode: 'correct' | 'resolve' | null
  onOpenChange: (open: boolean) => void
  onSubmit?: (field: FollowUpField, value: string) => void
  busy: boolean
  returnFocus?: () => void
}) {
  const defaultField = mode === 'resolve'
    ? detail.revisions.find((revision) => revision.state === 'conflicted')?.field ?? 'dueAt'
    : detail.revisions.find((revision) => revision.state === 'current')?.field ?? 'deliverable'
  const [field, setField] = useState<FollowUpField>(defaultField)
  const current = detail.revisions.find((revision) => revision.field === field && revision.state === 'current')
  const conflicts = detail.revisions.filter((revision) => revision.field === field && revision.state === 'conflicted')
  const [value, setValue] = useState(mode === 'resolve' ? conflicts[0]?.value ?? '' : current?.value ?? '')
  const title = mode === 'resolve' ? 'Resolve conflict' : 'Correct follow-up'
  const correctableFields = [...new Set(detail.revisions
    .filter((revision) => revision.state === 'current')
    .map((revision) => revision.field))]

  return (
    <Dialog open={mode !== null} onOpenChange={onOpenChange}>
      <DialogContent
        onCloseAutoFocus={(event) => {
          event.preventDefault()
          returnFocus?.()
        }}
      >
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>
            This decision becomes evidence and preserves the prior revision in the timeline.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-4">
          {mode === 'resolve' ? (
            <p className="text-sm text-muted-foreground">Conflicting values: {conflicts.map((revision) => revision.value).join(' · ')}</p>
          ) : current ? (
            <p className="text-sm text-muted-foreground">Current value: <span className="font-medium text-foreground">{current.value}</span>. Saving creates correction evidence and preserves this value in history.</p>
          ) : null}
          <div className="space-y-2">
            <Label htmlFor="continuity-field">Field</Label>
            <select
              id="continuity-field"
              className="h-10 w-full rounded-md border border-border bg-card px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              value={field}
              onChange={(event) => {
                const selected = event.target.value as FollowUpField
                setField(selected)
                setValue(detail.revisions.find((revision) => revision.field === selected && revision.state === 'current')?.value ?? '')
              }}
              disabled={mode === 'resolve'}
            >
              {(mode === 'resolve' ? [defaultField] : correctableFields).map((option) => (
                <option key={option} value={option}>{FIELD_LABEL[option]}</option>
              ))}
            </select>
          </div>
          <div className="space-y-2">
            <Label htmlFor="continuity-value">New value</Label>
            <Input
              id="continuity-value"
              value={value}
              onChange={(event) => setValue(event.target.value)}
              autoFocus
            />
          </div>
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>Cancel</Button>
          <Button
            disabled={busy || value.trim().length === 0}
            onClick={() => onSubmit?.(field, value.trim())}
          >
            {busy ? <Loader2 className="h-4 w-4 animate-spin motion-reduce:animate-none" aria-hidden /> : <Check className="h-4 w-4" aria-hidden />}
            {mode === 'resolve' ? 'Resolve' : 'Save correction'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function DetailSheet({
  detail,
  why,
  whyLoading,
  whyError,
  open,
  onOpenChange,
  onAccept,
  onCorrect,
  onResolve,
  onImportUpdate,
  busy,
  returnFocus,
}: {
  detail: FollowUpDetail | null
  why?: FollowUpWhy | null
  whyLoading?: boolean
  whyError?: boolean
  previewVolatile?: boolean
  open: boolean
  onOpenChange: (open: boolean) => void
  onAccept?: () => void
  onCorrect?: (field: FollowUpField, value: string) => void
  onResolve?: (field: FollowUpField, value: string) => void
  onImportUpdate?: (fixtureId: string) => void
  busy: boolean
  returnFocus?: () => void
}) {
  const [decisionMode, setDecisionMode] = useState<'correct' | 'resolve' | null>(null)
  const correctButtonRef = useRef<HTMLButtonElement>(null)
  if (!detail) return null
  const hasCandidates = detail.revisions.some((revision) => revision.state === 'candidate')
  const hasConflict = detail.revisions.some((revision) => revision.state === 'conflicted')
  const hasMonday = detail.revisions.some((revision) => revision.revisionId.includes('r1-monday'))
  const hasFridayConflict = detail.revisions.some((revision) => revision.revisionId.includes('r1-conflicting-friday'))
  const hasCompletion = detail.revisions.some((revision) => revision.field === 'completedAt')
  const nextUpdate = detail.status === 'tracked' && !hasCandidates
    ? !hasMonday
      ? { fixtureId: 'monday', label: 'Observe schedule update' }
      : !hasFridayConflict
        ? { fixtureId: 'conflicting-friday', label: 'Observe conflicting update' }
        : !hasCompletion
          ? { fixtureId: 'sent', label: 'Observe completion update' }
          : null
    : null

  return (
    <>
      <Sheet open={open} onOpenChange={onOpenChange}>
        <SheetContent
          side="right"
          className="w-full sm:max-w-xl"
          onCloseAutoFocus={(event) => {
            event.preventDefault()
            returnFocus?.()
          }}
        >
          <SheetHeader>
            <div className="flex items-center justify-between gap-3 pr-8">
              <div className="min-w-0">
                <SheetTitle className="truncate">
                  {detail.revisions.find((revision) => revision.field === 'deliverable' && revision.state === 'current')?.value ?? 'Follow-up detail'}
                </SheetTitle>
                <SheetDescription>Version {detail.version} · source-grounded continuity</SheetDescription>
              </div>
              <StatusBadge status={detail.status} />
            </div>
          </SheetHeader>
          <div className="flex-1 overflow-y-auto p-6">
            <Tabs defaultValue="overview">
              <TabsList aria-label="Follow-up detail views">
                <TabsTrigger value="overview">Detail</TabsTrigger>
                <TabsTrigger value="timeline"><History className="mr-1.5 h-4 w-4" aria-hidden />Timeline</TabsTrigger>
                <TabsTrigger value="why">Why</TabsTrigger>
              </TabsList>
              <TabsContent value="overview" className="mt-5"><Overview detail={detail} /></TabsContent>
              <TabsContent value="timeline" className="mt-5"><Timeline detail={detail} /></TabsContent>
              <TabsContent value="why" className="mt-5">
                <Why why={why} loading={whyLoading} failed={whyError} />
              </TabsContent>
            </Tabs>
          </div>
          <div className="flex flex-wrap gap-2 border-t border-border p-4">
            {nextUpdate ? (
              <Button
                variant="outline"
                onClick={() => onImportUpdate?.(nextUpdate.fixtureId)}
                disabled={busy}
              >
                <Sparkles className="h-4 w-4" aria-hidden />{nextUpdate.label}
              </Button>
            ) : null}
            {hasCandidates ? (
              <Button onClick={onAccept} disabled={busy}>
                <Check className="h-4 w-4" aria-hidden />Accept candidate
              </Button>
            ) : null}
            {hasConflict ? (
              <Button onClick={() => setDecisionMode('resolve')} disabled={busy}>
                <TriangleAlert className="h-4 w-4" aria-hidden />Resolve conflict
              </Button>
            ) : null}
            <Button
              ref={correctButtonRef}
              variant="outline"
              onClick={() => setDecisionMode('correct')}
              disabled={busy}
            >
              <Pencil className="h-4 w-4" aria-hidden />Correct
            </Button>
          </div>
        </SheetContent>
      </Sheet>
      <DecisionDialog
        key={decisionMode ?? 'closed'}
        detail={detail}
        mode={decisionMode}
        onOpenChange={(nextOpen) => { if (!nextOpen) setDecisionMode(null) }}
        onSubmit={(field, value) => {
          if (decisionMode === 'resolve') onResolve?.(field, value)
          else onCorrect?.(field, value)
          setDecisionMode(null)
        }}
        busy={busy}
        returnFocus={() => correctButtonRef.current?.focus()}
      />
    </>
  )
}

export interface FollowUpWorkspaceProps {
  view: ContinuityView
  items?: FollowUpSummary[]
  selected?: FollowUpDetail | null
  why?: FollowUpWhy | null
  whyLoading?: boolean
  whyError?: boolean
  previewVolatile?: boolean
  isLoading?: boolean
  detailLoading?: boolean
  listTruncated?: boolean
  errorMessage?: string | null
  busy?: boolean
  onViewChange?: (view: ContinuityView) => void
  onSelect?: (id: string) => void
  onCloseDetail?: () => void
  onAccept?: () => void
  onCorrect?: (field: FollowUpField, value: string) => void
  onResolve?: (field: FollowUpField, value: string) => void
  onImportUpdate?: (fixtureId: string) => void
  onImportInitial?: () => void
}

export function FollowUpWorkspace({
  view,
  items,
  selected,
  why,
  whyLoading,
  whyError,
  previewVolatile,
  isLoading,
  detailLoading,
  listTruncated,
  errorMessage,
  busy = false,
  onViewChange,
  onSelect,
  onCloseDetail,
  onAccept,
  onCorrect,
  onResolve,
  onImportUpdate,
  onImportInitial,
}: FollowUpWorkspaceProps) {
  const rows = items ?? []
  const detailReturnFocusRef = useRef<HTMLElement | null>(null)
  const selectFollowUp = (id: string) => {
    detailReturnFocusRef.current = document.activeElement instanceof HTMLElement
      ? document.activeElement
      : null
    onSelect?.(id)
  }
  return (
    <section aria-labelledby="continuity-title" className="space-y-6">
      <div>
        <h1 id="continuity-title" className="text-2xl font-semibold">Continuity</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Follow-ups that need a decision and the accepted state Tessera is tracking.
        </p>
      </div>

      {previewVolatile ? (
        <Alert variant="warning">
          <AlertDescription>
            Preview mode: changes are held in this browser session, disappear on reload, and are not durable Tessera state.
          </AlertDescription>
        </Alert>
      ) : null}

      {errorMessage ? (
        <Alert variant="destructive"><AlertDescription>{errorMessage}</AlertDescription></Alert>
      ) : null}
      {detailLoading ? (
        <Alert><AlertDescription>Loading follow-up detail and provenance…</AlertDescription></Alert>
      ) : null}
      {listTruncated ? (
        <Alert variant="warning"><AlertDescription>Showing the latest 100 follow-ups in this view.</AlertDescription></Alert>
      ) : null}

      <Tabs value={view} onValueChange={(value) => onViewChange?.(value as ContinuityView)}>
        <TabsList aria-label="Continuity views">
          <TabsTrigger value="attention">Attention</TabsTrigger>
          <TabsTrigger value="tracked">Tracked</TabsTrigger>
        </TabsList>
        <TabsContent value={view} className="mt-4">
          {isLoading ? (
            <div className="space-y-3" aria-label="Loading follow-ups">
              <Skeleton className="h-12 w-full" />
              <Skeleton className="h-12 w-full" />
              <Skeleton className="h-12 w-full" />
            </div>
          ) : rows.length === 0 ? (
            <div className="rounded-lg border border-dashed border-border px-4 py-10 text-center text-sm text-muted-foreground">
              <p>{view === 'attention'
                ? 'Nothing needs your attention.'
                : 'No accepted follow-ups are tracked yet.'}</p>
              {view === 'attention' && onImportInitial ? (
                <Button className="mt-4" variant="outline" onClick={onImportInitial} disabled={busy}>
                  <FileInput className="h-4 w-4" aria-hidden />Track example follow-up
                </Button>
              ) : null}
              {view === 'attention' && onImportInitial ? (
                <p className="mt-2 text-xs">Uses synthetic local evidence; no provider is connected.</p>
              ) : null}
            </div>
          ) : (
            <FollowUpTable items={rows} onSelect={selectFollowUp} />
          )}
        </TabsContent>
      </Tabs>

      <DetailSheet
        detail={selected ?? null}
        why={why}
        whyLoading={whyLoading}
        whyError={whyError}
        open={Boolean(selected)}
        onOpenChange={(open) => { if (!open) onCloseDetail?.() }}
        onAccept={onAccept}
        onCorrect={onCorrect}
        onResolve={onResolve}
        onImportUpdate={onImportUpdate}
        busy={busy}
        returnFocus={() => detailReturnFocusRef.current?.focus()}
      />
    </section>
  )
}