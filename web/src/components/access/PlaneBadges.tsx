import { Eye, Settings2, Zap } from 'lucide-react'
import { Badge } from '../ui/badge'
import { cn } from '../../lib/utils'
import type { ActionPlane } from '../../data/types'

// The action planes (ADR 0019) a delegation or module touches: observe (read),
// operate (use), or reshape (manage). Manage is the control plane — visually
// weightier because it defaults to a human step-up. Secret-free, presentational.

const PLANE_META: Record<ActionPlane, { label: string; title: string; className: string; Icon: typeof Eye }> = {
  read: {
    label: 'read',
    title: 'Observe — reads state without changing anything',
    className: 'text-muted-foreground',
    Icon: Eye,
  },
  use: {
    label: 'use',
    title: 'Operate — acts within the integration’s configured behaviour (the data plane)',
    className: 'text-health-live',
    Icon: Zap,
  },
  manage: {
    label: 'manage',
    title: 'Reshape — changes the integration itself (the control plane); defaults to a human step-up',
    className: 'text-health-expiring',
    Icon: Settings2,
  },
}

export interface PlaneBadgesProps {
  planes?: ActionPlane[]
  className?: string
}

/** Renders one chip per distinct plane, ordered read → use → manage. */
export function PlaneBadges({ planes, className }: PlaneBadgesProps) {
  if (!planes || planes.length === 0) {
    return null
  }

  return (
    <span className={cn('inline-flex flex-wrap gap-1.5', className)}>
      {planes.map((plane) => {
        const meta = PLANE_META[plane]
        if (!meta) {
          return null
        }
        const { label, title, className: tone, Icon } = meta
        return (
          <Badge key={plane} variant="outline" className={tone} title={title}>
            <Icon className="h-3 w-3" aria-hidden />
            {label}
          </Badge>
        )
      })}
    </span>
  )
}
