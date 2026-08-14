import { useId, useRef, useState, type MouseEvent, type ReactNode } from 'react'
import {
  Activity,
  Ban,
  CheckCircle2,
  ChevronLeft,
  Circle,
  Clock3,
  ExternalLink,
  FileText,
  HelpCircle,
  KeyRound,
  Laptop,
  Link2,
  ListChecks,
  Loader2,
  MoreHorizontal,
  Pause,
  RefreshCw,
  RotateCcw,
  ShieldCheck,
  TriangleAlert,
  Wifi,
  WifiOff,
  XCircle,
} from 'lucide-react'
import { cn } from '../../lib/utils'
import { Alert, AlertDescription, AlertTitle } from '../ui/alert'
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
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '../ui/dropdown-menu'
import { Input } from '../ui/input'
import { Label } from '../ui/label'
import { Skeleton } from '../ui/skeleton'

export type RemoteLifecycle =
  | 'PAIRING'
  | 'ONLINE'
  | 'BUSY'
  | 'DEGRADED'
  | 'OFFLINE'
  | 'REVOKED'
  | 'UPDATE_REQUIRED'

export type RemoteWorkspaceMode =
  | 'unsupported'
  | 'loading'
  | 'zero-hosts'
  | 'pairing-code'
  | 'pairing-review'
  | 'pairing-expired'
  | 'populated'
  | 'partial-error'

export type RemoteHostDetailState =
  | 'online-idle'
  | 'busy-running'
  | 'offline-waiting-for-host'
  | 'update-required'
  | 'revoked'
  | 'approval-required'
  | 'canceling'
  | 'succeeded-with-artifacts'
  | 'truncated-artifact'
  | 'expired-artifact'

export interface RemoteCurrentJob {
  runId: string
  name: string
  state: string
  href: string
  checkpoint?: string | null
  pendingApprovals?: number
}

export interface RemoteHostSummary {
  hostId: string
  href: string
  displayName: string
  platform: string
  architecture: string
  lifecycle: RemoteLifecycle
  agentVersion: string
  protocolVersion: string
  statusObservedAt: string
  lastSeenAt?: string | null
  capabilityCount: number
  resourceCount: number
  currentJob?: RemoteCurrentJob | null
}

export interface PairingRequestItem {
  id: string
  label: string
  detail: string
}

export interface RemotePairingCandidate {
  pairingId: string
  expiresAt: string
  hostName: string
  platform: string
  architecture: string
  protection: 'SECURE_ENCLAVE' | 'KEYCHAIN_THIS_DEVICE_ONLY'
  agentVersion: string
  protocolVersion: string
  capabilities: PairingRequestItem[]
  resources: PairingRequestItem[]
}

export interface RemoteCheckpoint {
  sequence: number
  step: string
  summary: string
  occurredAt: string
}

export interface RemoteArtifact {
  artifactId: string
  summary: string
  kind: 'TEXT'
  mediaType: 'text/plain'
  sizeBytes: number
  sha256: string
  retention: 'RUN'
  createdAt: string
  expiresAt?: string | null
  redacted: boolean
  truncated: boolean
  contentState: 'AVAILABLE' | 'EXPIRED'
  textContent?: string | null
}

export interface RemoteAccessItem {
  id: string
  label: string
  detail: string
}

export interface RemoteActivityItem {
  id: string
  summary: string
  occurredAt: string
}

const lifecycleMeta: Record<RemoteLifecycle, { label: string; icon: typeof Wifi; className: string }> = {
  PAIRING: { label: 'Pairing', icon: Link2, className: 'text-accent' },
  ONLINE: { label: 'Online', icon: Wifi, className: 'text-health-live' },
  BUSY: { label: 'Busy', icon: Loader2, className: 'text-health-expiring' },
  DEGRADED: { label: 'Degraded', icon: TriangleAlert, className: 'text-health-expiring' },
  OFFLINE: { label: 'Offline', icon: WifiOff, className: 'text-muted-foreground' },
  REVOKED: { label: 'Revoked', icon: Ban, className: 'text-muted-foreground' },
  UPDATE_REQUIRED: { label: 'Update required', icon: RefreshCw, className: 'text-health-expiring' },
}

function formatTime(value?: string | null): string {
  if (!value) return 'Not reported'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString()
}

function formatBytes(value: number): string {
  if (value < 1024) return `${value} B`
  return `${(value / 1024).toFixed(value % 1024 === 0 ? 0 : 1)} KiB`
}

function shortHash(value: string): string {
  return value.length > 18 ? `${value.slice(0, 10)}…${value.slice(-6)}` : value
}

function RemoteStatusBadge({ lifecycle }: { lifecycle: RemoteLifecycle }) {
  const meta = lifecycleMeta[lifecycle] ?? {
    label: 'Unknown',
    icon: HelpCircle,
    className: 'text-muted-foreground',
  }
  const Icon = meta.icon
  return (
    <Badge variant="outline" className={cn('shrink-0 gap-1.5 whitespace-nowrap forced-colors:border-[ButtonText]', meta.className)}>
      <Icon
        className={cn('h-3.5 w-3.5', lifecycle === 'BUSY' && 'animate-spin motion-reduce:animate-none')}
        aria-hidden
      />
      {meta.label}
    </Badge>
  )
}

function HostLink({ host, onOpen }: { host: RemoteHostSummary; onOpen?: (hostId: string) => void }) {
  const handleClick = (event: MouseEvent<HTMLAnchorElement>) => {
    if (!onOpen) return
    event.preventDefault()
    onOpen(host.hostId)
  }
  return (
    <a
      href={host.href}
      onClick={handleClick}
      className="inline-flex min-h-11 items-center font-medium text-foreground underline-offset-4 hover:underline focus-visible:rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
    >
      {host.displayName}
    </a>
  )
}

function HostActions({
  host,
  onOpen,
  onRevoke,
}: {
  host: RemoteHostSummary
  onOpen?: (hostId: string) => void
  onRevoke?: (host: RemoteHostSummary, returnFocusTo: HTMLElement | null) => void
}) {
  const triggerRef = useRef<HTMLButtonElement>(null)
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button ref={triggerRef} className="h-11 w-11" variant="ghost" size="icon" aria-label={`Actions for ${host.displayName}`}>
          <MoreHorizontal aria-hidden />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent>
        <DropdownMenuItem className="min-h-11" onSelect={() => onOpen?.(host.hostId)}>
          <Laptop aria-hidden />
          Open Host detail
        </DropdownMenuItem>
        {host.currentJob ? (
          <DropdownMenuItem className="min-h-11" asChild>
            <a href={host.currentJob.href}>
              <ExternalLink aria-hidden />
              View work
            </a>
          </DropdownMenuItem>
        ) : null}
        <DropdownMenuSeparator />
        <DropdownMenuItem
          className="min-h-11 text-health-error"
          disabled={host.lifecycle === 'REVOKED'}
          onSelect={() => onRevoke?.(host, triggerRef.current)}
        >
          <Ban aria-hidden />
          {host.lifecycle === 'REVOKED' ? 'Already revoked' : 'Revoke Host…'}
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

function HostInventory({
  hosts,
  onOpen,
  onRevoke,
}: {
  hosts: RemoteHostSummary[]
  onOpen?: (hostId: string) => void
  onRevoke?: (host: RemoteHostSummary, returnFocusTo: HTMLElement | null) => void
}) {
  return (
    <>
      <div className="hidden overflow-hidden rounded-lg border border-border bg-card lg:block" data-layout="desktop-table">
        <table className="w-full border-collapse text-sm">
          <caption className="sr-only">Paired Remote Hosts</caption>
          <thead>
            <tr className="border-b border-border">
              {['Host', 'Status', 'Current Job', 'Access', 'Last seen', 'Actions'].map((label) => (
                <th key={label} scope="col" className="h-10 px-3 text-left text-xs font-medium uppercase tracking-wide text-muted-foreground">
                  {label}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {hosts.map((host) => (
              <tr key={host.hostId} className="border-b border-border last:border-0">
                <th scope="row" className="px-3 py-3 text-left font-normal">
                  <HostLink host={host} onOpen={onOpen} />
                  <p className="text-xs font-normal text-muted-foreground">{host.platform} · {host.architecture}</p>
                  <p className="text-xs font-normal text-muted-foreground">Agent {host.agentVersion} · protocol {host.protocolVersion}</p>
                </th>
                <td className="px-3 py-3">
                  <RemoteStatusBadge lifecycle={host.lifecycle} />
                  <p className="mt-1 text-xs text-muted-foreground">Observed {formatTime(host.statusObservedAt)}</p>
                </td>
                <td className="max-w-64 px-3 py-3">
                  {host.currentJob ? (
                    <>
                      <p className="truncate font-medium">{host.currentJob.name}</p>
                      <p className="text-xs text-muted-foreground">{host.currentJob.state}</p>
                    </>
                  ) : <span className="text-muted-foreground">No Job assigned</span>}
                </td>
                <td className="px-3 py-3 text-muted-foreground">
                  {host.capabilityCount} capability · {host.resourceCount} resource
                  {` · ${host.currentJob?.pendingApprovals ?? 0} pending ${(host.currentJob?.pendingApprovals ?? 0) === 1 ? 'approval' : 'approvals'}`}
                </td>
                <td className="px-3 py-3 text-muted-foreground">{formatTime(host.lastSeenAt)}</td>
                <td className="px-3 py-3 text-right"><HostActions host={host} onOpen={onOpen} onRevoke={onRevoke} /></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <ul className="divide-y divide-border rounded-lg border border-border bg-card lg:hidden" aria-label="Paired Remote Hosts" data-layout="responsive-list">
        {hosts.map((host) => (
          <li key={host.hostId} className="p-4">
            <div className="flex items-start justify-between gap-3">
              <div className="min-w-0">
                <HostLink host={host} onOpen={onOpen} />
                <p className="text-xs text-muted-foreground">{host.platform} · {host.architecture}</p>
              </div>
              <RemoteStatusBadge lifecycle={host.lifecycle} />
            </div>
            <dl className="mt-3 grid grid-cols-[6.5rem_minmax(0,1fr)] gap-x-3 gap-y-2 text-sm">
              <dt className="text-muted-foreground">Current Job</dt>
              <dd className="min-w-0 break-words">{host.currentJob?.name ?? 'No Job assigned'}</dd>
              <dt className="text-muted-foreground">Access</dt>
              <dd>{host.capabilityCount} capability · {host.resourceCount} resource</dd>
              <dt className="text-muted-foreground">Status observed</dt>
              <dd>{formatTime(host.statusObservedAt)}</dd>
              <dt className="text-muted-foreground">Pending approvals</dt>
              <dd>{host.currentJob?.pendingApprovals ?? 0}</dd>
              <dt className="text-muted-foreground">Agent</dt>
              <dd>{host.agentVersion}</dd>
              <dt className="text-muted-foreground">Protocol</dt>
              <dd>{host.protocolVersion}</dd>
              <dt className="text-muted-foreground">Last seen</dt>
              <dd>{formatTime(host.lastSeenAt)}</dd>
            </dl>
            <div className="mt-3 flex justify-end"><HostActions host={host} onOpen={onOpen} onRevoke={onRevoke} /></div>
          </li>
        ))}
      </ul>
    </>
  )
}

function PairingDialog({
  mode,
  candidate,
  onDismiss,
  onCancel,
  onContinue,
  onConfirm,
  onRestart,
  onReturnFocus,
}: {
  mode: Extract<RemoteWorkspaceMode, 'pairing-code' | 'pairing-review' | 'pairing-expired'>
  candidate: RemotePairingCandidate
  onDismiss?: () => void
  onCancel?: () => void
  onContinue?: (code: string) => void
  onConfirm?: (selection: { displayName: string; capabilityIds: string[]; resourceIds: string[] }) => void
  onRestart?: () => void
  onReturnFocus?: () => void
}) {
  const [code, setCode] = useState('')
  const [displayName, setDisplayName] = useState(candidate.hostName)
  const [capabilityIds, setCapabilityIds] = useState(() => candidate.capabilities.map((item) => item.id))
  const [resourceIds, setResourceIds] = useState(() => candidate.resources.map((item) => item.id))
  const codeHintId = useId()
  const displayNameHintId = useId()
  const validCode = /^\d{6}$/.test(code)

  const toggle = (current: string[], id: string, setValue: (items: string[]) => void) => {
    setValue(current.includes(id) ? current.filter((item) => item !== id) : [...current, id])
  }

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onDismiss?.() }}>
      <DialogContent
        className="max-h-[calc(100vh-2rem)] overflow-y-auto forced-colors:border-[ButtonText]"
        onCloseAutoFocus={(event) => {
          if (!onReturnFocus) return
          event.preventDefault()
          onReturnFocus()
        }}
      >
        {mode === 'pairing-code' ? (
          <>
            <DialogHeader>
              <DialogTitle>Enter the code shown on your Mac</DialogTitle>
              <DialogDescription>Pairing expires {formatTime(candidate.expiresAt)}. Closing this window keeps the ticket active.</DialogDescription>
            </DialogHeader>
            <div className="space-y-2">
              <Label htmlFor="remote-pairing-code">Six-digit pairing code</Label>
              <Input
                id="remote-pairing-code"
                className="h-11 text-center font-mono text-xl tracking-[0.3em] tabular-nums"
                inputMode="numeric"
                autoComplete="one-time-code"
                maxLength={6}
                value={code}
                aria-describedby={codeHintId}
                onChange={(event) => setCode(event.target.value.replace(/\D/g, '').slice(0, 6))}
              />
              <p id={codeHintId} className="text-xs text-muted-foreground">
                {validCode ? 'Code ready for review.' : 'Enter all six digits shown on the Mac.'}
              </p>
            </div>
            <DialogFooter>
              <Button className="min-h-11" variant="outline" onClick={onCancel}>Cancel pairing</Button>
              <Button className="min-h-11" disabled={!validCode} onClick={() => onContinue?.(code)}>Continue</Button>
            </DialogFooter>
          </>
        ) : null}

        {mode === 'pairing-review' ? (
          <>
            <DialogHeader>
              <DialogTitle>Review this Mac</DialogTitle>
              <DialogDescription>Pairing identifies the Host. Capability and resource access are granted separately.</DialogDescription>
            </DialogHeader>
            <dl className="grid gap-3 text-sm sm:grid-cols-2">
              <div><dt className="text-muted-foreground">Platform</dt><dd>{candidate.platform} · {candidate.architecture}</dd></div>
              <div><dt className="text-muted-foreground">Protection</dt><dd className="flex items-center gap-1.5">{candidate.protection === 'SECURE_ENCLAVE' ? <ShieldCheck aria-hidden className="h-4 w-4" /> : <KeyRound aria-hidden className="h-4 w-4" />}{candidate.protection === 'SECURE_ENCLAVE' ? 'Secure Enclave' : 'This-device-only Keychain'}</dd></div>
              <div><dt className="text-muted-foreground">Agent</dt><dd>{candidate.agentVersion}</dd></div>
              <div><dt className="text-muted-foreground">Protocol</dt><dd>{candidate.protocolVersion}</dd></div>
            </dl>
            <div className="space-y-2">
              <Label htmlFor="remote-host-name">Friendly name</Label>
              <Input id="remote-host-name" className="h-11" value={displayName} aria-describedby={displayNameHintId} onChange={(event) => setDisplayName(event.target.value)} />
              <p id={displayNameHintId} className="text-xs text-muted-foreground">A friendly name is required before pairing.</p>
            </div>
            <fieldset className="rounded-lg border border-border p-3">
              <legend className="px-1 text-sm font-semibold">Requested capabilities</legend>
              {candidate.capabilities.map((item) => (
                <label key={item.id} className="flex min-h-11 items-start gap-3 py-2">
                  <input type="checkbox" className="mt-1 h-5 w-5" checked={capabilityIds.includes(item.id)} onChange={() => toggle(capabilityIds, item.id, setCapabilityIds)} />
                  <span><span className="block text-sm font-medium">{item.label}</span><span className="block text-xs text-muted-foreground">{item.detail}</span></span>
                </label>
              ))}
            </fieldset>
            <fieldset className="rounded-lg border border-border p-3">
              <legend className="px-1 text-sm font-semibold">Requested resources</legend>
              {candidate.resources.map((item) => (
                <label key={item.id} className="flex min-h-11 items-start gap-3 py-2">
                  <input type="checkbox" className="mt-1 h-5 w-5" checked={resourceIds.includes(item.id)} onChange={() => toggle(resourceIds, item.id, setResourceIds)} />
                  <span><span className="block text-sm font-medium">{item.label}</span><span className="block text-xs text-muted-foreground">{item.detail}</span></span>
                </label>
              ))}
            </fieldset>
            {capabilityIds.length === 0 || resourceIds.length === 0 ? (
              <Alert><AlertDescription>This Mac will be paired but cannot run Jobs until both capability and resource access are granted.</AlertDescription></Alert>
            ) : null}
            <DialogFooter>
              <Button className="min-h-11" variant="outline" onClick={onCancel}>Cancel pairing</Button>
              <Button
                className="min-h-11"
                disabled={!displayName.trim()}
                aria-describedby={!displayName.trim() ? displayNameHintId : undefined}
                onClick={() => onConfirm?.({ displayName: displayName.trim(), capabilityIds, resourceIds })}
              >
                Pair Mac
              </Button>
            </DialogFooter>
          </>
        ) : null}

        {mode === 'pairing-expired' ? (
          <>
            <DialogHeader>
              <DialogTitle>Pairing expired</DialogTitle>
              <DialogDescription>This pairing expired. No Host was paired.</DialogDescription>
            </DialogHeader>
            <Alert variant="destructive"><AlertDescription>Start a new pairing from both Tessera and the Mac.</AlertDescription></Alert>
            <DialogFooter>
              <Button className="min-h-11" variant="outline" onClick={onDismiss}>Close</Button>
              <Button className="min-h-11" onClick={onRestart}><RotateCcw aria-hidden />Start again</Button>
            </DialogFooter>
          </>
        ) : null}
      </DialogContent>
    </Dialog>
  )
}

function RevokeHostDialog({
  host,
  onClose,
  onConfirm,
  onReturnFocus,
}: {
  host: RemoteHostSummary | null
  onClose: () => void
  onConfirm?: (hostId: string) => void
  onReturnFocus?: () => void
}) {
  const [typedName, setTypedName] = useState('')
  const cancelRef = useRef<HTMLButtonElement>(null)
  const hintId = useId()
  const matched = typedName === host?.displayName
  const close = () => {
    setTypedName('')
    onClose()
  }
  return (
    <Dialog open={Boolean(host)} onOpenChange={(open) => { if (!open) close() }}>
      <DialogContent
        onOpenAutoFocus={(event) => { event.preventDefault(); cancelRef.current?.focus() }}
        onCloseAutoFocus={(event) => {
          if (!onReturnFocus) return
          event.preventDefault()
          onReturnFocus()
        }}
      >
        <DialogHeader>
          <DialogTitle>Revoke {host?.displayName}?</DialogTitle>
          <DialogDescription>New work will stop using this Mac immediately. If execution cannot be proven, the Job will require reconciliation. Historical Jobs, Actions, Evidence, artifacts, and Activity remain available.</DialogDescription>
        </DialogHeader>
        <div className="space-y-2">
          <Label htmlFor="remote-revoke-name">Type {host?.displayName} to confirm</Label>
          <Input id="remote-revoke-name" className="h-11" value={typedName} aria-describedby={hintId} onChange={(event) => setTypedName(event.target.value)} />
          <p id={hintId} className="text-xs text-muted-foreground">{matched ? 'Name matched. Revocation is enabled.' : 'Type the name exactly to enable.'}</p>
        </div>
        <DialogFooter>
          <Button ref={cancelRef} className="min-h-11" variant="outline" onClick={close}>Cancel</Button>
          <Button className="min-h-11" variant="destructive" disabled={!matched} onClick={() => { if (host) onConfirm?.(host.hostId); close() }}>Revoke Host</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function CancelJobDialog({
  open,
  job,
  onClose,
  onConfirm,
  onReturnFocus,
}: {
  open: boolean
  job?: RemoteCurrentJob | null
  onClose: () => void
  onConfirm?: () => void
  onReturnFocus?: () => void
}) {
  const cancelRef = useRef<HTMLButtonElement>(null)
  return (
    <Dialog open={open} onOpenChange={(nextOpen) => { if (!nextOpen) onClose() }}>
      <DialogContent
        onOpenAutoFocus={(event) => { event.preventDefault(); cancelRef.current?.focus() }}
        onCloseAutoFocus={(event) => {
          if (!onReturnFocus) return
          event.preventDefault()
          onReturnFocus()
        }}
      >
        <DialogHeader>
          <DialogTitle>Cancel {job?.name ?? 'this Job'}?</DialogTitle>
          <DialogDescription>Cancellation is a durable request. Completed work and history remain visible. If Tessera cannot prove the Host outcome, the Job will require reconciliation and will not be replayed automatically.</DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button ref={cancelRef} className="min-h-11" variant="outline" onClick={onClose}>Keep Job running</Button>
          <Button className="min-h-11" variant="destructive" onClick={() => { onConfirm?.(); onClose() }}>Cancel Job</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

export interface RemoteWorkspaceProps {
  mode: RemoteWorkspaceMode
  hosts?: RemoteHostSummary[]
  pairingCandidate?: RemotePairingCandidate
  partialErrorMessage?: string
  lastSuccessfulStatusAt?: string
  announcement?: string
  onRetry?: () => void
  onPair?: () => void
  onOpenHost?: (hostId: string) => void
  onRevokeHost?: (hostId: string) => void
  onDismissPairing?: () => void
  onCancelPairing?: () => void
  onContinuePairing?: (code: string) => void
  onConfirmPairing?: (selection: { displayName: string; capabilityIds: string[]; resourceIds: string[] }) => void
  onRestartPairing?: () => void
}

export function RemoteWorkspace({
  mode,
  hosts = [],
  pairingCandidate,
  partialErrorMessage,
  lastSuccessfulStatusAt,
  announcement,
  onRetry,
  onPair,
  onOpenHost,
  onRevokeHost,
  onDismissPairing,
  onCancelPairing,
  onContinuePairing,
  onConfirmPairing,
  onRestartPairing,
}: RemoteWorkspaceProps) {
  const [revokeHost, setRevokeHost] = useState<RemoteHostSummary | null>(null)
  const [revokeReturnFocus, setRevokeReturnFocus] = useState<HTMLElement | null>(null)
  const [pairingReturnFocus, setPairingReturnFocus] = useState<HTMLElement | null>(null)
  const pairingMode = mode === 'pairing-code' || mode === 'pairing-review' || mode === 'pairing-expired' ? mode : null
  const pairUnavailable = mode === 'unsupported' || mode === 'loading'
  const pairReasonId = useId()
  const beginPairing = (event: MouseEvent<HTMLButtonElement>) => {
    setPairingReturnFocus(event.currentTarget)
    onPair?.()
  }
  return (
    <section aria-labelledby="remote-workspace-title" className="space-y-5 remote-surface">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 id="remote-workspace-title" className="text-xl font-semibold leading-7">Remote Host preview</h1>
          <p className="mt-1 max-w-2xl text-sm text-muted-foreground">Pair trusted Macs and supervise canonical Jobs. Server Jobs continue normally with no Hosts configured.</p>
        </div>
        <Button className="min-h-11" onClick={beginPairing} disabled={pairUnavailable} aria-describedby={pairUnavailable ? pairReasonId : undefined}>
          <Link2 aria-hidden />Pair a Mac
        </Button>
      </div>
      <p id={pairReasonId} className="sr-only">Pairing is unavailable until the server capability check completes.</p>
      <p className="sr-only" role="status" aria-live="polite" aria-atomic="true">{announcement}</p>

      {mode === 'unsupported' ? (
        <Alert>
          <AlertTitle>Remote Hosts unavailable</AlertTitle>
          <AlertDescription className="mt-1">Remote Hosts are not available on this Tessera server.</AlertDescription>
          <Button className="mt-3 min-h-11" variant="outline" onClick={onRetry}><RefreshCw aria-hidden />Retry availability check</Button>
        </Alert>
      ) : null}

      {mode === 'loading' ? (
        <div aria-busy="true" aria-label="Checking Remote Host availability" className="space-y-3">
          <p className="text-sm text-muted-foreground">Checking Remote Host availability…</p>
          {[0, 1, 2, 3].map((item) => <Skeleton key={item} className="h-20 w-full" />)}
        </div>
      ) : null}

      {mode === 'zero-hosts' || pairingMode && hosts.length === 0 ? (
        <div className="rounded-lg border border-dashed border-border px-5 py-10 text-center">
          <Laptop className="mx-auto h-8 w-8 text-muted-foreground" aria-hidden />
          <h2 className="mt-3 font-semibold">No Macs are paired</h2>
          <p className="mx-auto mt-1 max-w-lg text-sm text-muted-foreground">Pair a Mac to supervise eligible Jobs. Server Jobs continue normally.</p>
          <Button className="mt-4 min-h-11" onClick={beginPairing}><Link2 aria-hidden />Pair a Mac</Button>
        </div>
      ) : null}

      {mode === 'partial-error' ? (
        <Alert variant="warning">
          <AlertTitle>Some Remote data is unavailable</AlertTitle>
          <AlertDescription className="mt-1">{partialErrorMessage ?? 'Hosts loaded, but current work and approval status could not be refreshed.'} Last successful Host status: {formatTime(lastSuccessfulStatusAt)}.</AlertDescription>
          <Button className="mt-3 min-h-11" variant="outline" onClick={onRetry}><RefreshCw aria-hidden />Retry missing data</Button>
        </Alert>
      ) : null}

      {(mode === 'populated' || mode === 'partial-error' || pairingMode && hosts.length > 0) ? (
        <>
          <HostInventory hosts={hosts} onOpen={onOpenHost} onRevoke={(host, returnFocusTo) => { setRevokeReturnFocus(returnFocusTo); setRevokeHost(host) }} />
          <p className="text-xs text-muted-foreground">{hosts.length} paired {hosts.length === 1 ? 'Host' : 'Hosts'} · status includes source timestamps</p>
        </>
      ) : null}

      {pairingMode && pairingCandidate ? (
        <PairingDialog
          mode={pairingMode}
          candidate={pairingCandidate}
          onDismiss={onDismissPairing}
          onCancel={onCancelPairing}
          onContinue={onContinuePairing}
          onConfirm={onConfirmPairing}
          onRestart={onRestartPairing}
          onReturnFocus={() => pairingReturnFocus?.focus()}
        />
      ) : null}
      <RevokeHostDialog
        host={revokeHost}
        onClose={() => setRevokeHost(null)}
        onConfirm={(hostId) => onRevokeHost?.(hostId)}
        onReturnFocus={() => revokeReturnFocus?.focus()}
      />
    </section>
  )
}

function DetailSection({ title, icon: Icon, children }: { title: string; icon: typeof Activity; children: ReactNode }) {
  const id = useId()
  return (
    <section className="border-t border-border py-5" aria-labelledby={id}>
      <h2 id={id} className="flex items-center gap-2 text-base font-semibold"><Icon className="h-4 w-4" aria-hidden />{title}</h2>
      <div className="mt-4">{children}</div>
    </section>
  )
}

function ArtifactList({ artifacts }: { artifacts: RemoteArtifact[] }) {
  const [preview, setPreview] = useState<RemoteArtifact | null>(null)
  return (
    <>
      {artifacts.length === 0 ? <p className="text-sm text-muted-foreground">No artifacts were retained for this run.</p> : (
        <ul className="divide-y divide-border" aria-label="Run artifacts">
          {artifacts.map((artifact) => {
            const expired = artifact.contentState === 'EXPIRED'
            const reasonId = `artifact-${artifact.artifactId}-reason`
            return (
              <li key={artifact.artifactId} className="py-3">
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div className="min-w-0 flex-1">
                    <p className="flex items-center gap-2 text-sm font-medium"><FileText className="h-4 w-4" aria-hidden />{artifact.summary}</p>
                    <p className="mt-1 break-words text-xs text-muted-foreground">{artifact.kind} · {artifact.mediaType} · {formatBytes(artifact.sizeBytes)} · SHA-256 <span className="font-mono">{shortHash(artifact.sha256)}</span></p>
                    <p className="mt-1 text-xs text-muted-foreground">Created {formatTime(artifact.createdAt)} · retention {artifact.retention}</p>
                    <div className="mt-2 flex flex-wrap gap-2">
                      {artifact.redacted ? <Badge variant="outline"><ShieldCheck aria-hidden />Redacted</Badge> : null}
                      {artifact.truncated ? <Badge variant="outline" className="text-health-expiring"><TriangleAlert aria-hidden />Truncated</Badge> : null}
                      {expired ? <Badge variant="outline"><Clock3 aria-hidden />Expired</Badge> : null}
                    </div>
                    {expired ? <p id={reasonId} className="mt-2 text-xs text-muted-foreground">This artifact expired {formatTime(artifact.expiresAt)}. Its metadata remains available.</p> : null}
                    {artifact.truncated ? <p className="mt-2 text-xs text-health-expiring">Tessera retained a bounded preview. Content beyond the server limit is unavailable.</p> : null}
                  </div>
                  <Button className="min-h-11" variant="outline" disabled={expired} aria-describedby={expired ? reasonId : undefined} onClick={() => setPreview(artifact)}>
                    Preview
                  </Button>
                </div>
              </li>
            )
          })}
        </ul>
      )}
      <Dialog open={Boolean(preview)} onOpenChange={(open) => { if (!open) setPreview(null) }}>
        <DialogContent className="max-h-[calc(100vh-2rem)] overflow-y-auto">
          <DialogHeader>
            <DialogTitle>{preview?.summary}</DialogTitle>
            <DialogDescription>Untrusted plain text. Tessera does not render HTML, Markdown, ANSI, links, or commands.</DialogDescription>
          </DialogHeader>
          <pre className="max-h-80 overflow-auto whitespace-pre-wrap break-all rounded-md border border-border bg-muted p-3 font-mono text-xs leading-5">{preview?.textContent ?? 'No preview content is available.'}</pre>
        </DialogContent>
      </Dialog>
    </>
  )
}

export interface RemoteHostDetailProps {
  state: RemoteHostDetailState
  host: RemoteHostSummary
  currentJob?: RemoteCurrentJob | null
  blocker?: string | null
  approval?: ReactNode
  checkpoints?: RemoteCheckpoint[]
  artifacts?: RemoteArtifact[]
  capabilities?: RemoteAccessItem[]
  resources?: RemoteAccessItem[]
  activity?: RemoteActivityItem[]
  announcement?: string
  onPause?: () => void
  onCancel?: () => void
  onRevoke?: (hostId: string) => void
}

export function RemoteHostDetail({
  state,
  host,
  currentJob = host.currentJob,
  blocker,
  approval,
  checkpoints = [],
  artifacts = [],
  capabilities = [],
  resources = [],
  activity = [],
  announcement,
  onPause,
  onCancel,
  onRevoke,
}: RemoteHostDetailProps) {
  const [revokeOpen, setRevokeOpen] = useState(false)
  const [cancelOpen, setCancelOpen] = useState(false)
  const revokeTriggerRef = useRef<HTMLButtonElement>(null)
  const cancelTriggerRef = useRef<HTMLButtonElement>(null)
  const pauseReason = state === 'offline-waiting-for-host'
    ? 'No Host step is running. Pause this Job from Jobs.'
    : state === 'update-required'
      ? 'The Host cannot reach a checkpoint until it is updated.'
      : state === 'approval-required'
        ? 'The Job is already waiting for approval.'
        : state === 'canceling'
          ? 'Cancellation is already requested.'
          : null
  const pauseReasonId = useId()
  const canceling = state === 'canceling'
  const terminal = state === 'succeeded-with-artifacts' || state === 'truncated-artifact' || state === 'expired-artifact'

  return (
    <article aria-labelledby="remote-host-title" className="remote-surface">
      <a href="/remote" className="inline-flex min-h-11 items-center gap-2 text-sm text-muted-foreground underline-offset-4 hover:underline focus-visible:rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"><ChevronLeft aria-hidden />Remote Hosts</a>
      <div className="mt-3 flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 id="remote-host-title" className="text-xl font-semibold leading-7">{host.displayName}</h1>
          <p className="mt-1 text-sm text-muted-foreground">{host.platform} · {host.architecture} · observed {formatTime(host.statusObservedAt)}</p>
        </div>
        <RemoteStatusBadge lifecycle={host.lifecycle} />
      </div>
      <p className="sr-only" role="status" aria-live="polite" aria-atomic="true">{announcement}</p>

      <dl className="mt-5 grid gap-3 border-t border-border py-5 text-sm sm:grid-cols-2 lg:grid-cols-4">
        <div><dt className="text-muted-foreground">Host ID</dt><dd className="break-all font-mono text-xs">{host.hostId}</dd></div>
        <div><dt className="text-muted-foreground">Agent</dt><dd>{host.agentVersion}</dd></div>
        <div><dt className="text-muted-foreground">Protocol</dt><dd>{host.protocolVersion}</dd></div>
        <div><dt className="text-muted-foreground">Last seen</dt><dd>{formatTime(host.lastSeenAt)}</dd></div>
      </dl>

      <DetailSection title="Current work" icon={Laptop}>
        {blocker ? <Alert variant="warning"><AlertTitle>Blocked</AlertTitle><AlertDescription>{blocker}</AlertDescription></Alert> : null}
        {state === 'canceling' ? <Alert className="mt-3"><AlertDescription>Cancel requested. Waiting for the Host to stop at a safe checkpoint.</AlertDescription></Alert> : null}
        {currentJob ? (
          <div className="mt-3 flex flex-wrap items-start justify-between gap-3">
            <div><p className="font-medium">{currentJob.name}</p><p className="text-sm text-muted-foreground">{currentJob.state}{currentJob.checkpoint ? ` · ${currentJob.checkpoint}` : ''}</p></div>
            <Button className="min-h-11" variant="outline" asChild><a href={currentJob.href}><ExternalLink aria-hidden />View work</a></Button>
          </div>
        ) : <p className="text-sm text-muted-foreground">No Job is assigned to this Host.</p>}
        {currentJob && !terminal ? (
          <div className="mt-4 flex flex-wrap gap-2">
            <Button className="min-h-11" variant="outline" disabled={Boolean(pauseReason)} aria-describedby={pauseReason ? pauseReasonId : undefined} onClick={onPause}><Pause aria-hidden />Pause after current step</Button>
            <Button ref={cancelTriggerRef} className="min-h-11" variant="outline" disabled={canceling} aria-describedby={canceling ? pauseReasonId : undefined} onClick={() => setCancelOpen(true)}><XCircle aria-hidden />{canceling ? 'Canceling…' : 'Cancel Job'}</Button>
          </div>
        ) : null}
        {pauseReason ? <p id={pauseReasonId} className="mt-2 text-xs text-muted-foreground">{pauseReason}</p> : null}
      </DetailSection>

      {approval ? <DetailSection title="Action required" icon={ShieldCheck}>{approval}</DetailSection> : null}

      <DetailSection title="Durable progress" icon={ListChecks}>
        {checkpoints.length === 0 ? <p className="text-sm text-muted-foreground">No checkpoints have been recorded.</p> : (
          <ol className="divide-y divide-border" aria-label="Host Job checkpoints">
            {checkpoints.map((checkpoint) => (
              <li key={checkpoint.sequence} className="grid grid-cols-[1rem_minmax(0,1fr)] gap-3 py-3">
                <CheckCircle2 className="mt-0.5 h-4 w-4 text-health-live" aria-hidden />
                <div><p className="text-sm font-medium">{checkpoint.summary}</p><p className="mt-1 text-xs text-muted-foreground">{checkpoint.step} · {formatTime(checkpoint.occurredAt)}</p></div>
              </li>
            ))}
          </ol>
        )}
      </DetailSection>

      <DetailSection title="Artifacts" icon={FileText}><ArtifactList artifacts={artifacts} /></DetailSection>

      <DetailSection title="Granted access" icon={KeyRound}>
        <div className="grid gap-5 md:grid-cols-2">
          <div><h3 className="text-sm font-semibold">Capabilities</h3><ul className="mt-2 divide-y divide-border">{capabilities.map((item) => <li key={item.id} className="py-2"><p className="text-sm font-medium">{item.label}</p><p className="text-xs text-muted-foreground">{item.detail}</p></li>)}</ul>{capabilities.length === 0 ? <p className="mt-2 text-sm text-muted-foreground">No capabilities granted.</p> : null}</div>
          <div><h3 className="text-sm font-semibold">Resources</h3><ul className="mt-2 divide-y divide-border">{resources.map((item) => <li key={item.id} className="py-2"><p className="text-sm font-medium">{item.label}</p><p className="text-xs text-muted-foreground">{item.detail}</p></li>)}</ul>{resources.length === 0 ? <p className="mt-2 text-sm text-muted-foreground">No resources granted.</p> : null}</div>
        </div>
      </DetailSection>

      <DetailSection title="Activity" icon={Activity}>
        {activity.length === 0 ? <p className="text-sm text-muted-foreground">No Host activity has been recorded.</p> : (
          <ol className="divide-y divide-border" aria-label="Host activity">
            {activity.map((item) => <li key={item.id} className="grid grid-cols-[1rem_minmax(0,1fr)] gap-3 py-3"><Circle className="mt-1 h-2.5 w-2.5 fill-current text-muted-foreground" aria-hidden /><div><p className="text-sm font-medium">{item.summary}</p><time className="text-xs text-muted-foreground" dateTime={item.occurredAt}>{formatTime(item.occurredAt)}</time></div></li>)}
          </ol>
        )}
      </DetailSection>

      <div className="flex justify-end border-t border-border pt-5">
        <Button ref={revokeTriggerRef} className="min-h-11" variant="destructive" disabled={state === 'revoked'} onClick={() => setRevokeOpen(true)}>
          <Ban aria-hidden />{state === 'revoked' ? 'Host revoked' : 'Revoke Host…'}
        </Button>
      </div>
      <RevokeHostDialog host={revokeOpen ? host : null} onClose={() => setRevokeOpen(false)} onConfirm={onRevoke} onReturnFocus={() => revokeTriggerRef.current?.focus()} />
      <CancelJobDialog open={cancelOpen} job={currentJob} onClose={() => setCancelOpen(false)} onConfirm={onCancel} onReturnFocus={() => cancelTriggerRef.current?.focus()} />
    </article>
  )
}