import type { ReactNode } from 'react'
import { CheckCircle2, CircleAlert, Loader2, PauseCircle, ShieldOff, TimerReset } from 'lucide-react'
import { cn } from '../../lib/utils'
import type { LiveViewHandle } from '../../data/types'
import { Button } from '../ui/button'
import { Skeleton } from '../ui/skeleton'
import { HandoffTaskList } from './HandoffTaskList'
import { LiveViewIframe, type LiveViewMessage } from './LiveViewIframe'
import { PreflightDialog } from './PreflightDialog'
import { StatusPill } from './StatusPill'
import { TargetIdentityStrip } from './TargetIdentityStrip'
import type { HandoffStatus } from './handoff-machine'

const TRUST_LINE = 'We keep the session. We never see your password.'

export interface LiveHandoffStageProps {
  status: HandoffStatus
  handle: LiveViewHandle | null
  provider: string
  ownerPrincipal: string
  /** Verified hostname for the strip before/without a handle (falls back to handle). */
  hostname?: string
  unavailableReason?: string | null
  errorReason?: string | null
  onStart?: () => void
  onCancel?: () => void
  onManualDone?: () => void
  onRestart?: () => void
  onPopOut?: () => void
  onBackToAccounts?: () => void
  onGetHelp?: () => void
  onIframeReady?: () => void
  onSessionEvent?: (message: LiveViewMessage) => void
  onExpire?: () => void
}

/** The persistent trust line — always visible, on the live stage and every panel. */
function TrustLine({ className }: { className?: string }) {
  return <p className={cn('text-sm text-muted-foreground', className)}>{TRUST_LINE}</p>
}

/** Centered outcome/explainer panel shared by Done / Expired / Unavailable / Error / Paused. */
function StatePanel({
  icon,
  iconClassName,
  title,
  body,
  actions,
}: {
  icon: ReactNode
  iconClassName?: string
  title: string
  body: ReactNode
  actions?: ReactNode
}) {
  return (
    <div className="flex min-h-[420px] flex-col items-center justify-center gap-4 px-6 py-12 text-center md:min-h-[480px]">
      <span
        className={cn(
          'flex h-14 w-14 items-center justify-center rounded-2xl bg-muted text-muted-foreground',
          iconClassName,
        )}
      >
        {icon}
      </span>
      <div className="space-y-1.5">
        <h2 className="text-lg font-semibold">{title}</h2>
        <div className="mx-auto max-w-md text-sm text-muted-foreground">{body}</div>
      </div>
      {actions ? <div className="flex flex-wrap items-center justify-center gap-2">{actions}</div> : null}
      <TrustLine className="pt-2" />
    </div>
  )
}

/** A dim overlay over the canvas for Connecting / Verifying / Reconnecting waits. */
function CanvasOverlay({ icon, label }: { icon: ReactNode; label: string }) {
  return (
    <div className="absolute inset-0 flex flex-col items-center justify-center gap-3 bg-card/70 backdrop-blur-[1px]">
      <span className="text-accent">{icon}</span>
      <p className="text-sm font-medium text-foreground">{label}</p>
    </div>
  )
}

function LiveStage({
  status,
  handle,
  provider,
  hostname,
  onManualDone,
  onPopOut,
  onIframeReady,
  onSessionEvent,
  onExpire,
}: LiveHandoffStageProps) {
  const manualDoneEnabled = status === 'waiting' || status === 'reconnecting'
  return (
    <div className="flex flex-col overflow-hidden rounded-xl border border-border bg-card">
      <TargetIdentityStrip
        provider={provider}
        hostname={hostname}
        handle={handle}
        status={status}
        onExpire={onExpire}
        onPopOut={onPopOut}
      />

      <div className="flex flex-col md:flex-row">
        {/* The canvas: a real window, never a fake progress bar (anti-pattern #5). */}
        <div className="relative min-h-[320px] flex-1 bg-muted md:min-h-[460px]">
          {handle ? (
            <LiveViewIframe
              handle={handle}
              onMessage={onSessionEvent ?? (() => undefined)}
              onReady={onIframeReady}
            />
          ) : (
            <Skeleton className="absolute inset-0 rounded-none" />
          )}
          {status === 'connecting' ? (
            <CanvasOverlay icon={<Loader2 className="h-6 w-6 animate-spin" />} label="Connecting…" />
          ) : null}
          {status === 'verifying' ? (
            <CanvasOverlay
              icon={<Loader2 className="h-6 w-6 animate-spin" />}
              label="We're confirming the session is live."
            />
          ) : null}
          {status === 'reconnecting' ? (
            <CanvasOverlay icon={<Loader2 className="h-6 w-6 animate-spin" />} label="Reconnecting…" />
          ) : null}
        </div>

        <aside className="flex shrink-0 flex-col justify-between gap-6 border-t border-border p-4 md:w-64 md:border-l md:border-t-0">
          <HandoffTaskList status={status} />
          <StatusPill status={status} />
        </aside>
      </div>

      {/* Persistent trust line + the manual "I'm done" fallback (spec R2). */}
      <div className="flex flex-wrap items-center justify-between gap-3 border-t border-border px-4 py-3">
        <TrustLine />
        <Button variant="outline" onClick={onManualDone} disabled={!manualDoneEnabled}>
          I'm done
        </Button>
      </div>
    </div>
  )
}

/**
 * The Live hand-off stage — the crown jewel. Renders one reachable state of the
 * status machine (spec C.2). Pure presentation: it takes the machine's state and
 * fires callbacks; the page (useHandoff) owns orchestration. Every wait state shows
 * the live canvas or a real status — never an infinite spinner (anti-pattern #3).
 */
export function LiveHandoffStage(props: LiveHandoffStageProps) {
  const { status, provider, ownerPrincipal, unavailableReason, errorReason } = props

  if (status === 'preflight') {
    return (
      <>
        <div className="flex min-h-[420px] items-center justify-center rounded-xl border border-dashed border-border bg-card px-6 text-center md:min-h-[480px]">
          <p className="text-sm text-muted-foreground">Preparing the secure window…</p>
        </div>
        <PreflightDialog
          open
          provider={provider}
          onStart={props.onStart ?? (() => undefined)}
          onCancel={props.onCancel ?? (() => undefined)}
        />
      </>
    )
  }

  if (status === 'unavailable') {
    return (
      <StatePanel
        icon={<ShieldOff className="h-7 w-7" aria-hidden />}
        title="Live hand-off isn't set up yet"
        body={
          <>
            <p>
              This connection can't be seeded from the portal until a live-browser worker
              is wired. Nothing failed — it's switched off by default.
            </p>
            {unavailableReason ? (
              <p className="pt-2 text-xs text-muted-foreground/80">{unavailableReason}</p>
            ) : null}
          </>
        }
        actions={
          <Button variant="outline" onClick={props.onBackToAccounts}>
            Back to My accounts
          </Button>
        }
      />
    )
  }

  if (status === 'done') {
    return (
      <StatePanel
        icon={<CheckCircle2 className="h-7 w-7" aria-hidden />}
        iconClassName="bg-health-live/10 text-health-live"
        title="Session live"
        body={
          <p>
            {provider} is connected for {ownerPrincipal}.
          </p>
        }
        actions={<Button onClick={props.onBackToAccounts}>Back to My accounts</Button>}
      />
    )
  }

  if (status === 'expired') {
    return (
      <StatePanel
        icon={<TimerReset className="h-7 w-7" aria-hidden />}
        title="This window timed out"
        body={<p>No problem — starting again is quick.</p>}
        actions={<Button onClick={props.onRestart}>Start again</Button>}
      />
    )
  }

  if (status === 'paused') {
    return (
      <StatePanel
        icon={<PauseCircle className="h-7 w-7" aria-hidden />}
        title="Paused — you can resume"
        body={
          <p>
            We saved your place for {provider}. Pick up where you left off whenever you're ready.
          </p>
        }
        actions={<Button onClick={props.onRestart}>Resume</Button>}
      />
    )
  }

  if (status === 'error') {
    return (
      <StatePanel
        icon={<CircleAlert className="h-7 w-7" aria-hidden />}
        iconClassName="bg-health-error/10 text-health-error"
        title="We couldn't verify the session"
        body={
          errorReason ? <p>{errorReason}</p> : <p>Something interrupted the hand-off. Try again.</p>
        }
        actions={
          <>
            <Button onClick={props.onRestart}>Try again</Button>
            {props.onGetHelp ? (
              <Button variant="outline" onClick={props.onGetHelp}>
                Get help
              </Button>
            ) : null}
          </>
        }
      />
    )
  }

  return <LiveStage {...props} />
}
