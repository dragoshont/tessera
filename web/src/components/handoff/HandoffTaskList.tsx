import { Check } from 'lucide-react'
import { cn } from '../../lib/utils'
import type { HandoffStatus } from './handoff-machine'

const STEPS = [
  { id: 1, label: 'Log in' },
  { id: 2, label: 'Solve the checkbox' },
  { id: 3, label: 'We take over' },
] as const

// How far the human has progressed, derived from the status. While they work
// (Connecting/Waiting/Reconnecting) steps ①② are live; the broker's verify owns
// ③ (Verifying), and Done checks all three.
function completedThrough(status: HandoffStatus): number {
  if (status === 'done') return 3
  if (status === 'verifying') return 2
  return 0
}

function currentStep(status: HandoffStatus): number {
  if (status === 'done') return 4
  if (status === 'verifying') return 3
  return 1
}

/**
 * The right-rail task list: ① Log in · ② Solve the checkbox · ③ We take over.
 * The active step is highlighted; completed steps get a check. Greyed entirely
 * while the canvas is still connecting.
 */
export function HandoffTaskList({
  status,
  className,
}: {
  status: HandoffStatus
  className?: string
}) {
  const completed = completedThrough(status)
  const current = currentStep(status)
  const dimmed = status === 'connecting'

  return (
    <div className={cn('flex flex-col gap-3', className)}>
      <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">What to do</p>
      <ol className={cn('flex flex-col gap-3', dimmed && 'opacity-50')}>
        {STEPS.map((step) => {
          const isComplete = step.id <= completed
          const isActive = step.id === current && !dimmed
          return (
            <li key={step.id} className="flex items-center gap-3">
              <span
                aria-hidden
                className={cn(
                  'flex h-6 w-6 shrink-0 items-center justify-center rounded-full border text-xs font-semibold',
                  isComplete
                    ? 'border-transparent bg-health-live text-white'
                    : isActive
                      ? 'border-accent text-accent'
                      : 'border-border text-muted-foreground',
                )}
              >
                {isComplete ? <Check className="h-3.5 w-3.5" /> : step.id}
              </span>
              <span
                className={cn(
                  'text-sm',
                  isActive ? 'font-medium text-foreground' : 'text-muted-foreground',
                )}
              >
                {step.label}
              </span>
            </li>
          )
        })}
      </ol>
    </div>
  )
}
