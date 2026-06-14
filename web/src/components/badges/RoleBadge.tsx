import { ShieldCheck } from 'lucide-react'
import type { Role } from '../../data/types'
import { Badge } from '../ui/badge'
import { cn } from '../../lib/utils'

export function RoleBadge({ role, className }: { role: Role; className?: string }) {
  if (role === 'Admin') {
    return (
      <Badge className={cn('border-transparent bg-accent/15 text-accent', className)}>
        <ShieldCheck className="h-3 w-3" aria-hidden />
        Admin
      </Badge>
    )
  }
  return (
    <Badge variant="secondary" className={className}>
      Member
    </Badge>
  )
}
