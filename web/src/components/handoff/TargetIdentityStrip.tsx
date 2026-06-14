import { ExternalLink, Lock } from 'lucide-react'
import type { LiveViewHandle } from '../../data/types'
import { cn } from '../../lib/utils'
import { ProviderIcon } from '../common/ProviderIcon'
import { Button } from '../ui/button'
import { Countdown } from './Countdown'
import type { HandoffStatus } from './handoff-machine'

export interface TargetIdentityStripProps {
  provider: string
  /** Server-verified hostname (the anti-phishing anchor). */
  hostname?: string
  handle: LiveViewHandle | null
  status: HandoffStatus
  onExpire?: () => void
  onPopOut?: () => void
  className?: string
}

/**
 * The top strip: lock + a local ProviderIcon tile + the VERIFIED hostname +
 * countdown + Pop out. Tessera chrome is clearly *around* the browser, never *as*
 * it (anti-pattern #11). We deliberately render a local icon, never the handle's
 * remote/spoofable favicon (anti-pattern #4 / favicon-trust gap).
 */
export function TargetIdentityStrip({
  provider,
  hostname,
  handle,
  status,
  onExpire,
  onPopOut,
  className,
}: TargetIdentityStripProps) {
  const verifiedHost = hostname ?? handle?.targetHostname
  return (
    <div
      className={cn(
        'flex flex-wrap items-center gap-x-3 gap-y-2 border-b border-border bg-card px-4 py-2.5',
        className,
      )}
    >
      <ProviderIcon provider={provider} className="h-6 w-6 text-sm" />
      {verifiedHost ? (
        <span className="flex items-center gap-1.5 text-sm font-medium">
          <Lock className="h-3.5 w-3.5 text-health-live" aria-hidden />
          {verifiedHost}
          <span className="font-normal text-muted-foreground">— verified</span>
        </span>
      ) : (
        <span className="text-sm text-muted-foreground">Preparing the secure window…</span>
      )}

      <div className="ml-auto flex items-center gap-3">
        {handle ? (
          <Countdown
            expiresAt={handle.expiresAt}
            running={status === 'waiting' || status === 'reconnecting'}
            onExpire={onExpire}
          />
        ) : null}
        <Button variant="ghost" size="sm" onClick={onPopOut} disabled={!handle}>
          <ExternalLink className="h-3.5 w-3.5" aria-hidden />
          Pop out
        </Button>
      </div>
    </div>
  )
}
