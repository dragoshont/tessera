import { useEffect, useMemo, useRef, useState } from 'react'
import { Clock } from 'lucide-react'
import { cn } from '../../lib/utils'

function formatRemaining(ms: number): string {
  const totalSeconds = Math.max(0, Math.ceil(ms / 1000))
  const minutes = Math.floor(totalSeconds / 60)
  const seconds = totalSeconds % 60
  return `${minutes}:${seconds.toString().padStart(2, '0')}`
}

export interface CountdownProps {
  /** ISO instant the handle expires. */
  expiresAt: string
  /** Frozen when false (Verifying); keeps running on Reconnecting (honest) — spec C.2. */
  running?: boolean
  /** Below this many seconds the countdown turns amber (LowTimeWarning). */
  warnAtSeconds?: number
  /** Fired once when a running countdown reaches 0 → Expired. */
  onExpire?: () => void
  className?: string
}

/**
 * A hand-rolled countdown to `expiresAt` — never a fake/synthetic progress bar
 * (anti-pattern #5). Always visible on short-TTL sessions so expiry is never a
 * surprise (anti-pattern #13); on hitting 0 it hands off to a friendly re-arm.
 */
export function Countdown({
  expiresAt,
  running = true,
  warnAtSeconds = 60,
  onExpire,
  className,
}: CountdownProps) {
  const target = useMemo(() => new Date(expiresAt).getTime(), [expiresAt])
  const [now, setNow] = useState(() => Date.now())
  const hasFired = useRef(false)

  useEffect(() => {
    if (!running) return
    const interval = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(interval)
  }, [running])

  const remaining = target - now

  useEffect(() => {
    if (!running) return
    if (remaining > 0) {
      hasFired.current = false
      return
    }
    if (!hasFired.current) {
      hasFired.current = true
      onExpire?.()
    }
  }, [remaining, running, onExpire])

  const isWarning = remaining <= warnAtSeconds * 1000
  const label = formatRemaining(remaining)

  return (
    <span
      className={cn(
        'inline-flex items-center gap-1 tabular-nums',
        isWarning ? 'text-health-expiring' : 'text-muted-foreground',
        className,
      )}
      aria-label={`${label} left`}
    >
      <Clock className="h-3.5 w-3.5" aria-hidden />
      {label} left
    </span>
  )
}
