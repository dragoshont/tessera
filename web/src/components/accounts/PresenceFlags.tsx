import { Check, Minus } from 'lucide-react'
import type { Connection } from '../../data/types'
import { cn } from '../../lib/utils'

function PresenceFlag({ present, label }: { present: boolean; label: string }) {
  return (
    <div className="flex items-center gap-2">
      {present ? (
        <Check className="h-4 w-4 text-health-live" aria-hidden />
      ) : (
        <Minus className="h-4 w-4 text-muted-foreground" aria-hidden />
      )}
      <span className={cn('text-sm', present ? 'text-foreground' : 'text-muted-foreground')}>
        {present ? 'has ' : 'no '}
        {label}
      </span>
    </div>
  )
}

/**
 * Bundle-field PRESENCE only — never a value, never a reveal/copy control.
 * The contract carries booleans, so it is structurally impossible to render a
 * secret here. The never-reveal line is shown verbatim, every time.
 */
export function PresenceFlags({ connection }: { connection: Connection }) {
  return (
    <div className="flex flex-col gap-2">
      <PresenceFlag present={connection.hasCookies} label="cookies" />
      <PresenceFlag present={connection.hasRefreshToken} label="refresh token" />
      <PresenceFlag present={connection.hasAccessToken} label="access token" />
      <p className="pt-1 text-sm italic text-muted-foreground">
        Tessera can't show this — that's the point.
      </p>
    </div>
  )
}
