import type { Connection } from '../../data/types'
import { formatExpiry, isExpiryUncertain } from '../../lib/format'
import { cn } from '../../lib/utils'

/** Renders the honest expiry value; uncertain values ("~estimated"/"unknown") read muted. */
export function ExpiryCell({
  connection,
  className,
}: {
  connection: Connection
  className?: string
}) {
  const text = formatExpiry(connection)
  return (
    <span
      className={cn(isExpiryUncertain(text) ? 'text-muted-foreground' : 'text-foreground', className)}
    >
      {text}
    </span>
  )
}
