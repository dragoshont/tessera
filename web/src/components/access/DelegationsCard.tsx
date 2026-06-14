import { Bot, ShieldCheck, User } from 'lucide-react'
import { Badge } from '../ui/badge'
import { Skeleton } from '../ui/skeleton'
import { PlaneBadges } from './PlaneBadges'
import { cn } from '../../lib/utils'
import type { Delegation } from '../../data/types'

// "Who/what may act as me" (ADR 0017) — a projection of the enforced grants.
// Presentational: the page supplies the scoped list. Secret-free (ids + globs).

function DelegationRow({ delegation }: { delegation: Delegation }) {
  const caller = shortCaller(delegation.caller)
  return (
    <li className="flex flex-col gap-2 border-b border-border/60 py-3 last:border-0 sm:flex-row sm:items-start sm:justify-between">
      <div className="min-w-0">
        <div className="flex items-center gap-2">
          {delegation.isAutomation ? (
            <Bot className="h-4 w-4 text-muted-foreground" aria-hidden />
          ) : (
            <User className="h-4 w-4 text-muted-foreground" aria-hidden />
          )}
          <span className="truncate font-medium">{caller}</span>
          {delegation.isAutomation ? (
            <Badge variant="secondary">automation</Badge>
          ) : null}
        </div>
        <p className="mt-0.5 text-xs text-muted-foreground">
          may act on <span className="font-medium text-foreground">{delegation.displayName}</span>
          {delegation.onBehalfOf ? <> · as {delegation.onBehalfOf}</> : null}
          {delegation.owner ? <> · {ownerHint(delegation.owner)}</> : null}
        </p>
      </div>
      <div className="flex flex-wrap items-center gap-1.5 sm:justify-end">
        <PlaneBadges planes={delegation.planes} />
        {delegation.actions.map((action) => (
          <Badge key={action} variant="outline">
            {action}
          </Badge>
        ))}
        {delegation.stepUpActions.map((action) => (
          <Badge
            key={`stepup-${action}`}
            variant="outline"
            className="text-health-expiring"
            title="Requires your confirmation before it can run"
          >
            <ShieldCheck className="h-3 w-3" aria-hidden />
            {action}
          </Badge>
        ))}
      </div>
    </li>
  )
}

function shortCaller(caller: string): string {
  const slash = caller.lastIndexOf('/')
  return slash >= 0 ? caller.slice(slash + 1) : caller
}

/** A short hint of whose credential backs the delegation (ADR 0020). */
function ownerHint(owner: NonNullable<Delegation['owner']>): string {
  switch (owner) {
    case 'user':
      return 'via your login'
    case 'service':
      return 'via a household key'
    case 'dependent':
      return 'via a dependent’s login'
    default:
      return ''
  }
}

export interface DelegationsCardProps {
  delegations?: Delegation[]
  isLoading?: boolean
  /** Shown when nothing may act as the scoped person — the reassuring empty state. */
  emptyHint?: string
  className?: string
}

export function DelegationsCard({ delegations, isLoading, emptyHint, className }: DelegationsCardProps) {
  if (isLoading) {
    return <Skeleton className={cn('h-32 w-full', className)} />
  }

  const list = delegations ?? []
  if (list.length === 0) {
    return (
      <p className={cn('rounded-lg border border-dashed border-border px-4 py-8 text-center text-sm text-muted-foreground', className)}>
        {emptyHint ?? 'Nothing can act on your behalf. No agent or automation has been granted access.'}
      </p>
    )
  }

  return (
    <ul className={cn('rounded-lg border border-border bg-card px-4', className)}>
      {list.map((delegation, index) => (
        <DelegationRow key={`${delegation.caller}-${delegation.target}-${index}`} delegation={delegation} />
      ))}
    </ul>
  )
}
