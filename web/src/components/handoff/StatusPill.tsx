import { cn } from '../../lib/utils'
import type { HandoffStatus } from './handoff-machine'

interface PillVisual {
  label: string
  text: string
  dot: string
  pulse?: boolean
}

// One pill per status token (spec C.2). Verifying uses the accent; "Couldn't
// verify" is the only red. Absent-style neutrals stay quiet.
const VISUALS: Record<HandoffStatus, PillVisual> = {
  preflight: { label: 'Ready', text: 'text-muted-foreground', dot: 'bg-muted-foreground' },
  connecting: { label: 'Connecting…', text: 'text-muted-foreground', dot: 'bg-muted-foreground' },
  waiting: { label: 'Waiting for you', text: 'text-health-expiring', dot: 'bg-health-expiring' },
  verifying: { label: 'Verifying…', text: 'text-accent', dot: 'bg-accent', pulse: true },
  done: { label: 'Done', text: 'text-health-live', dot: 'bg-health-live' },
  reconnecting: {
    label: 'Reconnecting…',
    text: 'text-health-expiring',
    dot: 'bg-health-expiring',
    pulse: true,
  },
  expired: { label: 'Expired', text: 'text-muted-foreground', dot: 'bg-muted-foreground' },
  paused: { label: 'Paused', text: 'text-muted-foreground', dot: 'bg-muted-foreground' },
  unavailable: { label: 'Not set up', text: 'text-muted-foreground', dot: 'bg-muted-foreground' },
  error: { label: "Couldn't verify", text: 'text-health-error', dot: 'bg-health-error' },
}

export function StatusPill({ status, className }: { status: HandoffStatus; className?: string }) {
  const visual = VISUALS[status]
  return (
    <span
      className={cn('inline-flex items-center gap-2 text-sm font-medium', visual.text, className)}
      aria-label={`Status: ${visual.label}`}
      role="status"
    >
      <span
        aria-hidden
        className={cn('h-2.5 w-2.5 rounded-full', visual.dot, visual.pulse && 'animate-pulse')}
      />
      {visual.label}
    </span>
  )
}
