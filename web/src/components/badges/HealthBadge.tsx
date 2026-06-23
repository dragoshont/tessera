import { AlertTriangle, HelpCircle, XCircle, type LucideIcon } from 'lucide-react'
import type { ConnectionStatus } from '../../data/types'
import { cn } from '../../lib/utils'

interface HealthVisual {
  label: string
  /** text color token */
  text: string
  /** dot background (filled) or border (outline for the neutral Absent state) */
  dot: string
  icon?: LucideIcon
  pulse?: boolean
}

// Absent is intentionally neutral gray (a to-do), never red. Red is error/dead only.
// `live` is the ONLY green — presence alone never earns it (ADR 0025).
const VISUALS: Record<ConnectionStatus, HealthVisual> = {
  live: { label: 'Live', text: 'text-health-live', dot: 'bg-health-live' },
  // Present, but Tessera has not confirmed it is alive — amber caution, never green.
  unverified: { label: 'Unverified', text: 'text-health-expiring', dot: 'bg-health-expiring', icon: HelpCircle },
  expiring_soon: { label: 'Expiring soon', text: 'text-health-expiring', dot: 'bg-health-expiring' },
  absent: {
    label: 'Absent',
    text: 'text-health-absent',
    dot: 'border border-health-absent bg-transparent',
  },
  error: { label: 'Error', text: 'text-health-error', dot: 'bg-health-error', icon: AlertTriangle },
  // A verdict confirmed the session is dead — red, distinct from an unreadable-store `error`.
  dead: { label: 'Dead', text: 'text-health-error', dot: 'bg-health-error', icon: XCircle },
  seeding: { label: 'Seeding', text: 'text-accent', dot: 'bg-accent', pulse: true },
  needs_human: { label: 'Needs you', text: 'text-health-expiring', dot: 'bg-health-expiring', pulse: true },
}

// Fail closed: a status the UI does not recognise (e.g. a backend that adds one)
// renders a neutral "Unknown" — NEVER green and never a crash (ADR 0025: unknown ⇒ degraded).
const UNKNOWN_FALLBACK: HealthVisual = {
  label: 'Unknown',
  text: 'text-health-absent',
  dot: 'border border-health-absent bg-transparent',
  icon: HelpCircle,
}

export function HealthBadge({
  status,
  className,
}: {
  status: ConnectionStatus
  className?: string
}) {
  const visual = (VISUALS as Record<string, HealthVisual>)[status] ?? UNKNOWN_FALLBACK
  const Icon = visual.icon
  return (
    <span
      className={cn('inline-flex items-center gap-1.5 text-sm font-medium', visual.text, className)}
      aria-label={`Health: ${visual.label}`}
    >
      {Icon ? (
        <Icon className="h-3.5 w-3.5" aria-hidden />
      ) : (
        <span
          aria-hidden
          className={cn('h-2.5 w-2.5 rounded-full', visual.dot, visual.pulse && 'animate-pulse')}
        />
      )}
      <span>{visual.label}</span>
    </span>
  )
}
