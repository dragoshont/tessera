import type { HTMLAttributes } from 'react'
import { cn } from '../../lib/utils'

/** Quiet initials avatar — no remote image, so nothing user/recipe-supplied can render. */
export function Avatar({
  name,
  className,
  ...props
}: HTMLAttributes<HTMLDivElement> & { name: string }) {
  const initials =
    name
      .replace(/@.*/, '')
      .split(/[.\s_-]+/)
      .filter(Boolean)
      .slice(0, 2)
      .map((part) => part[0]?.toUpperCase() ?? '')
      .join('') ||
    name[0]?.toUpperCase() ||
    '?'

  return (
    <div
      aria-hidden
      className={cn(
        'flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-muted text-xs font-medium text-muted-foreground',
        className,
      )}
      {...props}
    >
      {initials}
    </div>
  )
}
