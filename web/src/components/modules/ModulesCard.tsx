import { Globe, ShieldOff } from 'lucide-react'
import { Badge } from '../ui/badge'
import { Skeleton } from '../ui/skeleton'
import { cn } from '../../lib/utils'
import type { Module } from '../../data/types'

// "What modules (connectors) are loaded" (ADR 0017) — a projection of recipes +
// egress posture + usage counts. Secret-free: host only, never a path. The egress
// badge is honest about whether the module can actually reach upstream right now.

function EgressBadge({ module }: { module: Module }) {
  if (module.egress === 'none') {
    return (
      <Badge variant="secondary" title="Status-only: the broker makes no upstream call">
        <ShieldOff className="h-3 w-3" aria-hidden />
        status-only
      </Badge>
    )
  }
  return (
    <Badge
      variant="outline"
      className={module.egressEnabled ? 'text-health-live' : 'text-muted-foreground'}
      title={
        module.egressEnabled
          ? 'HTTP egress is enabled — the broker can reach upstream'
          : 'HTTP recipe, but the global egress gate is off — it cannot reach upstream'
      }
    >
      <Globe className="h-3 w-3" aria-hidden />
      {module.egressEnabled ? 'egress on' : 'egress off'}
    </Badge>
  )
}

function ModuleRow({ module }: { module: Module }) {
  return (
    <li className="flex flex-col gap-2 border-b border-border/60 py-3 last:border-0 sm:flex-row sm:items-start sm:justify-between">
      <div className="min-w-0">
        <div className="flex items-center gap-2">
          <span className="truncate font-medium">{module.displayName}</span>
          <EgressBadge module={module} />
        </div>
        <p className="mt-0.5 text-xs text-muted-foreground">
          {module.driver} driver
          {module.upstreamHost ? <> · {module.upstreamHost}</> : null} ·{' '}
          {module.connectionCount} {module.connectionCount === 1 ? 'connection' : 'connections'}
          {module.toolCount > 0 ? <> · {module.toolCount} tools</> : null}
        </p>
      </div>
      <div className="flex flex-wrap gap-1.5 sm:justify-end">
        {module.actions.length === 0 ? (
          <span className="text-xs text-muted-foreground">no actions</span>
        ) : (
          module.actions.map((action) => (
            <Badge key={action} variant="outline">
              {action}
            </Badge>
          ))
        )}
      </div>
    </li>
  )
}

export interface ModulesCardProps {
  modules?: Module[]
  isLoading?: boolean
  className?: string
}

export function ModulesCard({ modules, isLoading, className }: ModulesCardProps) {
  if (isLoading) {
    return <Skeleton className={cn('h-32 w-full', className)} />
  }

  const list = modules ?? []
  if (list.length === 0) {
    return (
      <p className={cn('rounded-lg border border-dashed border-border px-4 py-8 text-center text-sm text-muted-foreground', className)}>
        No modules are loaded yet.
      </p>
    )
  }

  return (
    <ul className={cn('rounded-lg border border-border bg-card px-4', className)}>
      {list.map((module) => (
        <ModuleRow key={module.target} module={module} />
      ))}
    </ul>
  )
}
