import {
  Ban,
  Check,
  CircleAlert,
  CircleCheck,
  Clock,
  Loader2,
  Play,
  SquareTerminal,
  SquarePen,
  ShieldCheck,
  TriangleAlert,
  X,
} from 'lucide-react'
import type { Conversation, DevelopmentWorkspace, Job } from '@tessera/client'
import type { R2Action, R2JobRunDetail } from '../../api/r2'
import { cn } from '../../lib/utils'
import { recoveryMessage } from '../../lib/product-error'
import { Alert, AlertDescription } from '../ui/alert'
import { Badge } from '../ui/badge'
import { Button } from '../ui/button'

const STATE_META: Record<string, { label: string; icon: typeof Clock; className: string }> = {
  PROPOSED: { label: 'Approval required', icon: ShieldCheck, className: 'text-health-expiring' },
  AUTHORIZED: { label: 'Authorized', icon: Play, className: 'text-health-expiring' },
  STARTED: { label: 'Running', icon: Loader2, className: 'text-health-expiring' },
  EXECUTION_SUCCEEDED: { label: 'Executed', icon: Check, className: 'text-health-live' },
  PROVIDER_VERIFIED: { label: 'Provider verified', icon: CircleCheck, className: 'text-health-live' },
  EXTERNALLY_CONFIRMED: { label: 'Verified', icon: CircleCheck, className: 'text-health-live' },
  RECONCILIATION_REQUIRED: { label: 'Outcome unknown', icon: TriangleAlert, className: 'text-accent' },
  FAILED: { label: 'Failed', icon: CircleAlert, className: 'text-health-error' },
  CANCELED: { label: 'Canceled', icon: Ban, className: 'text-muted-foreground' },
  EXPIRED: { label: 'Expired', icon: Clock, className: 'text-muted-foreground' },
  WAITING_FOR_APPROVAL: { label: 'Waiting for approval', icon: ShieldCheck, className: 'text-health-expiring' },
  SUCCEEDED: { label: 'Succeeded', icon: CircleCheck, className: 'text-health-live' },
  RUNNING: { label: 'Running', icon: Loader2, className: 'text-health-expiring' },
  QUEUED: { label: 'Queued', icon: Clock, className: 'text-muted-foreground' },
  BLOCKED: { label: 'Blocked', icon: TriangleAlert, className: 'text-accent' },
}

export function ProductStateBadge({ state }: { state: string }) {
  const meta = STATE_META[state] ?? { label: state.replaceAll('_', ' ').toLowerCase(), icon: Clock, className: 'text-muted-foreground' }
  const Icon = meta.icon
  return (
    <Badge variant="outline" className={cn('gap-1.5 capitalize', meta.className)} data-product-state={state}>
      <Icon className={cn('h-3.5 w-3.5', state === 'RUNNING' || state === 'STARTED' ? 'animate-spin motion-reduce:animate-none' : '')} aria-hidden />
      {meta.label}
    </Badge>
  )
}

export function DevelopmentTaskCreator({
  conversations,
  workspaces,
  conversationId,
  workspaceId,
  conversationsLoading = false,
  workspacesLoading = false,
  submitting = false,
  errorCode = null,
  onConversationChange,
  onWorkspaceChange,
  onSubmit,
  onRefresh,
}: {
  conversations: Conversation[]
  workspaces: DevelopmentWorkspace[]
  conversationId: string
  workspaceId: string
  conversationsLoading?: boolean
  workspacesLoading?: boolean
  submitting?: boolean
  errorCode?: string | null
  onConversationChange: (value: string) => void
  onWorkspaceChange: (value: string) => void
  onSubmit: () => void
  onRefresh?: () => void
}) {
  const selectedWorkspace = workspaces.find((item) => item.id === workspaceId)
  const blocked = errorCode === 'development_executor_unavailable'
  const unavailable = errorCode === 'workspace_unavailable'
  const errorMessage = blocked
    ? "The server's development executor is not configured."
    : unavailable
      ? 'The selected workspace is no longer available. Choose another ready workspace.'
      : errorCode
        ? recoveryMessage(errorCode)
        : null
  return (
    <section className="grid gap-3 border-b border-border py-5 md:grid-cols-2" aria-labelledby="development-task-heading">
      <div className="md:col-span-2">
        <div className="flex items-center gap-2">
          <SquareTerminal className="h-4 w-4" aria-hidden />
          <h2 id="development-task-heading" className="text-base font-semibold">Run repository status</h2>
        </div>
        <p className="mt-1 text-sm text-muted-foreground">Read one immutable server snapshot in the selected conversation. This command makes no repository changes.</p>
      </div>
      <label className="space-y-2 text-sm font-medium">
        Conversation
        <select
          className="h-10 w-full rounded-md border border-border bg-card px-3 text-sm"
          value={conversationId}
          disabled={conversationsLoading || submitting}
          onChange={(event) => onConversationChange(event.target.value)}
        >
          <option value="">{conversationsLoading ? 'Loading conversations…' : 'Choose conversation'}</option>
          {conversations.map((item) => <option key={item.id} value={item.id}>{item.title}</option>)}
        </select>
      </label>
      <label className="space-y-2 text-sm font-medium">
        Workspace
        <select
          className="h-10 w-full rounded-md border border-border bg-card px-3 text-sm"
          value={workspaceId}
          disabled={!conversationId || workspacesLoading || submitting || workspaces.length === 0}
          onChange={(event) => onWorkspaceChange(event.target.value)}
        >
          <option value="">{workspacesLoading ? 'Loading workspaces…' : 'Choose workspace'}</option>
          {workspaces.map((item) => <option key={item.id} value={item.id}>{item.displayName}</option>)}
        </select>
        {selectedWorkspace ? <span className="block break-all font-mono text-xs font-normal text-muted-foreground">Snapshot {selectedWorkspace.snapshotHash}</span> : null}
      </label>
      <div className="flex flex-wrap items-center justify-between gap-3 border-y border-border py-3 md:col-span-2">
        <div>
          <p className="text-sm font-medium">Repository status</p>
          <p className="font-mono text-xs text-muted-foreground">repository.status</p>
        </div>
        <Badge variant="outline" className="gap-1.5 text-muted-foreground"><ShieldCheck className="h-3.5 w-3.5" aria-hidden />Read only</Badge>
      </div>
      <div className="md:col-span-2" role="status" aria-live="polite">
        {!conversationsLoading && conversations.length === 0 ? <p className="text-sm text-muted-foreground">No conversations yet. Create a conversation in Chat before running repository status.</p> : null}
        {conversationId && !workspacesLoading && workspaces.length === 0 ? <p className="text-sm text-muted-foreground">No ready workspaces for this conversation. Ask the server operator to provision a repository snapshot.</p> : null}
        {workspacesLoading ? <p className="text-sm text-muted-foreground">Loading workspaces…</p> : null}
        {errorMessage ? <Alert variant={blocked ? 'warning' : 'destructive'}><AlertDescription>{errorMessage}</AlertDescription></Alert> : null}
      </div>
      <div className="flex flex-wrap items-center justify-end gap-2 md:col-span-2">
        {onRefresh && conversationId && workspaces.length === 0 && !workspacesLoading ? <Button type="button" variant="outline" onClick={onRefresh}>Refresh workspaces</Button> : null}
        <Button type="button" disabled={!conversationId || !workspaceId || submitting || blocked} onClick={onSubmit}>
          {submitting ? <Loader2 className="animate-spin motion-reduce:animate-none" aria-hidden /> : <Play aria-hidden />}
          {submitting ? 'Starting repository status…' : 'Run repository status'}
        </Button>
      </div>
    </section>
  )
}

export function ActionApprovalCard({
  action,
  accountLabel,
  busy,
  error,
  onApprove,
  onCancel,
  onEdit,
}: {
  action: R2Action
  accountLabel?: string
  busy?: boolean
  error?: string | null
  onApprove?: () => void
  onCancel?: () => void
  onEdit?: () => void
}) {
  const pending = action.state === 'PROPOSED'
  const payload = action.payloadPreview && typeof action.payloadPreview === 'object' && !Array.isArray(action.payloadPreview) ? action.payloadPreview as Record<string, unknown> : {}
  const value = (name: string) => {
    const item = payload[name]
    if (Array.isArray(item)) return item.join(', ')
    return typeof item === 'string' || typeof item === 'number' ? String(item) : null
  }
  const isGmail = action.pluginId === 'gmail'
  const isReginaMaria = action.pluginId === 'regina-maria'
  const details = isGmail ? [
    ['From',value('from')],['To',value('to')],['Cc',value('cc')],['Bcc',value('bcc')],['Subject',value('subject')],['Body',value('body')],
  ] : isReginaMaria ? [
    ['Appointment',value('appointmentId') ?? value('oldAppointmentId')],['Doctor',value('doctor')],['Specialty',value('specialty')],['Service',value('service')],['Location',value('location')],['Date',value('date')],['Time',value('time')],['Mode',value('mode')],['Displayed cost',[value('price'),value('currency')].filter(Boolean).join(' ') || null],
  ] : []
  const consequence = action.capabilityId === 'github.issues.create'
    ? 'Creates a GitHub issue in the target repository.'
    : action.capabilityId === 'local.memory.remember'
      ? 'Adds user-authored state to Tessera Memory and future context.'
      : action.capabilityId === 'local.memory.correct'
        ? 'Supersedes the current Memory value while preserving history.'
        : action.capabilityId === 'gmail.messages.send'
          ? 'Sends this exact message from the selected Gmail account.'
          : action.capabilityId === 'gmail.drafts.create' || action.capabilityId === 'gmail.drafts.update'
            ? 'Creates or updates this exact Gmail draft without sending it.'
            : action.capabilityId === 'reginamaria.appointment.book'
              ? 'Confirms this exact appointment in Regina Maria.'
              : action.capabilityId === 'reginamaria.appointment.reschedule'
                ? 'Moves the selected Regina Maria appointment to this exact slot.'
                : action.capabilityId === 'reginamaria.appointment.cancel'
                  ? 'Cancels the selected Regina Maria appointment.'
                  : `Executes ${action.capabilityId} against ${action.target}.`
  return (
    <section className="border-y border-border py-4" aria-labelledby={`action-${action.id}`}>
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <p className="text-xs font-medium uppercase text-muted-foreground">Consequential action</p>
          <h3 id={`action-${action.id}`} className="mt-1 font-semibold">{action.capabilityId}</h3>
          <p className="mt-1 text-sm text-muted-foreground">{action.pluginId}@{action.pluginVersion}</p>
        </div>
        <ProductStateBadge state={action.state} />
      </div>
      <dl className="mt-4 grid gap-3 text-sm sm:grid-cols-2">
        <div className="sm:col-span-2"><dt className="text-muted-foreground">Consequence</dt><dd className="font-medium">{consequence}</dd></div>
        <div><dt className="text-muted-foreground">Target</dt><dd className="break-all font-medium">{action.target}</dd></div>
        <div><dt className="text-muted-foreground">Account</dt><dd>{accountLabel ?? action.accountId ?? 'No account'}</dd></div>
        <div><dt className="text-muted-foreground">Capability version</dt><dd>{action.capabilityVersion}</dd></div>
        <div><dt className="text-muted-foreground">Approval expires</dt><dd>{action.expiresAt ? new Date(action.expiresAt).toLocaleString() : 'Not available'}</dd></div>
      </dl>
      {details.some(([,detail])=>detail) ? <dl className="mt-4 grid gap-x-6 gap-y-3 border-t border-border pt-4 text-sm sm:grid-cols-2">{details.filter(([,detail])=>detail).map(([label,detail])=><div key={label} className={label==='Body'?'sm:col-span-2':''}><dt className="text-muted-foreground">{label}</dt><dd className="whitespace-pre-wrap font-medium">{detail}</dd></div>)}</dl> : null}
      <div className="mt-4">
        <p className="text-xs font-medium uppercase text-muted-foreground">Exact payload</p>
        <pre className="mt-2 max-h-52 overflow-auto rounded-md border border-border bg-muted p-3 text-xs whitespace-pre-wrap break-all">{JSON.stringify(action.payloadPreview, null, 2)}</pre>
      </div>
      {action.providerReceipt ? <p className="mt-3 text-xs text-muted-foreground">Receipt: <span className="font-mono">{action.providerReceipt}</span></p> : null}
      {action.verificationState ? <p className="mt-1 text-xs text-muted-foreground">Verification: {action.verificationState}</p> : null}
      {action.failureCode ? <Alert variant="destructive" className="mt-3"><AlertDescription>{recoveryMessage(action.failureCode)}</AlertDescription></Alert> : null}
      {error ? <Alert variant="destructive" className="mt-3"><AlertDescription>{error}</AlertDescription></Alert> : null}
      {pending ? (
        <div className="mt-4 flex flex-wrap gap-2">
          <Button className="min-h-11" onClick={onApprove} disabled={busy}><ShieldCheck aria-hidden />Approve exact action</Button>
          {onEdit ? <Button className="min-h-11" variant="outline" onClick={onEdit} disabled={busy}><SquarePen aria-hidden />Edit as new proposal</Button> : null}
          <Button className="min-h-11" variant="outline" onClick={onCancel} disabled={busy}><X aria-hidden />Cancel</Button>
        </div>
      ) : null}
      {action.state === 'RECONCILIATION_REQUIRED' ? (
        <Alert variant="warning" className="mt-4"><AlertDescription>Provider outcome is unknown. Tessera will not retry this action until it is reconciled.</AlertDescription></Alert>
      ) : null}
    </section>
  )
}

export function JobRunTimeline({ detail,job,onApprove,onCancel,busy=false }: { detail: R2JobRunDetail;job?:Job|null;onApprove?:(action:R2Action)=>void;onCancel?:(action:R2Action)=>void;busy?:boolean }) {
  const development = job?.kind === 'DEVELOPMENT' ? job.developmentSpec : null
  return (
    <section aria-labelledby={`run-${detail.run.id}`}>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div><h3 id={`run-${detail.run.id}`} className="font-semibold">Run {detail.run.id.slice(0, 8)}</h3><p className="text-sm text-muted-foreground">Scheduled {new Date(detail.run.scheduledFor).toLocaleString()}</p></div>
        <ProductStateBadge state={detail.run.state} />
      </div>
      <dl className="mt-4 grid gap-3 text-sm sm:grid-cols-3">
        {development ? <><div><dt className="text-muted-foreground">Command</dt><dd className="font-mono text-xs">{development.commandProfile}</dd></div><div><dt className="text-muted-foreground">Workspace</dt><dd className="break-all font-mono text-xs">{development.workspaceId}</dd></div></> : <><div><dt className="text-muted-foreground">Model profile</dt><dd>{detail.run.modelProfileId ?? 'Not recorded'}</dd></div><div><dt className="text-muted-foreground">Context snapshot</dt><dd className="break-all font-mono text-xs">{detail.contextSnapshot?.snapshotRef ?? 'No context selected'}</dd></div></>}
        <div><dt className="text-muted-foreground">Error</dt><dd>{detail.run.errorCode ? recoveryMessage(detail.run.errorCode) : 'None'}</dd></div>
      </dl>
      <ol className="mt-4 divide-y divide-border" aria-label="Job execution trace">
        {detail.trace.items.length === 0 ? <li className="py-4 text-sm text-muted-foreground">No execution steps have been recorded yet.</li> : detail.trace.items.map((entry) => (
          <li key={entry.sequence} className="grid grid-cols-[1rem_1fr] gap-3 py-3">
            <span className="mt-1.5 h-2 w-2 rounded-full bg-muted-foreground" aria-hidden />
            <div><p className="text-sm font-medium">{entry.summary}</p><p className="mt-1 text-xs text-muted-foreground">{new Date(entry.occurredAt).toLocaleString()}</p></div>
          </li>
        ))}
      </ol>
      {!development ? <><section className="border-t border-border py-3"><h4 className="text-sm font-semibold">Capability and account use</h4>{detail.capabilityUses.items.length===0?<p className="mt-2 text-xs text-muted-foreground">No capabilities recorded.</p>:<ul className="mt-2 space-y-2 text-xs">{detail.capabilityUses.items.map((call)=><li key={call.callId}><span className="font-medium">{call.capabilityId}@{call.capabilityVersion}</span> · {call.state} · account {call.accountId??'none'}{call.errorCode?` · ${recoveryMessage(call.errorCode)}`:''}</li>)}</ul>}</section><section className="border-t border-border py-3"><h4 className="text-sm font-semibold">Evidence</h4>{detail.evidence.items.length===0?<p className="mt-2 text-xs text-muted-foreground">No Evidence recorded.</p>:<ul className="mt-2 space-y-2 text-xs">{detail.evidence.items.map((item)=><li key={item.evidenceId}><span className="font-medium">{item.sourceType}</span> · {item.boundedExcerpt??item.sourceLocator}</li>)}</ul>}</section></> : null}
      {detail.outputs.items.map((output) => <div key={output.outputRef} className="border-t border-border py-3"><div className="flex flex-wrap items-center justify-between gap-2"><p className="text-sm font-medium">{output.kind === 'DEVELOPMENT_LOG' ? 'Repository status log' : output.summary}</p><span className="text-xs text-muted-foreground">{new Date(output.createdAt).toLocaleString()}</span></div>{output.text ? <pre className="mt-2 max-h-80 overflow-auto whitespace-pre-wrap break-words rounded-md border border-border bg-muted p-3 font-mono text-xs leading-5">{output.text}</pre> : <p className="mt-2 text-xs text-muted-foreground">No output.</p>}{output.truncated ? <p className="mt-2 flex items-center gap-1.5 text-xs text-health-expiring"><TriangleAlert className="h-3.5 w-3.5" aria-hidden />Output truncated at the server limit.</p> : null}</div>)}
      {detail.actions.items.map((action) => <ActionApprovalCard key={action.id} action={action} busy={busy} onApprove={onApprove?()=>onApprove(action):undefined} onCancel={onCancel?()=>onCancel(action):undefined} />)}
    </section>
  )
}
