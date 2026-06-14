import { cva, type VariantProps } from 'class-variance-authority'
import type { HTMLAttributes } from 'react'
import { cn } from '../../lib/utils'

const alertVariants = cva('relative w-full rounded-lg border px-4 py-3 text-sm', {
  variants: {
    variant: {
      default: 'border-border bg-card text-foreground',
      // Amber, not red — "needs attention" is a to-do, not a failure.
      warning: 'border-health-expiring/30 bg-health-expiring/10 text-foreground',
      // Red is reserved for true error only.
      destructive: 'border-health-error/30 bg-health-error/10 text-foreground',
    },
  },
  defaultVariants: {
    variant: 'default',
  },
})

export interface AlertProps
  extends HTMLAttributes<HTMLDivElement>,
    VariantProps<typeof alertVariants> {}

export function Alert({ className, variant, ...props }: AlertProps) {
  return <div role="alert" className={cn(alertVariants({ variant }), className)} {...props} />
}

export function AlertTitle({ className, ...props }: HTMLAttributes<HTMLHeadingElement>) {
  return <h5 className={cn('mb-0.5 font-medium leading-none', className)} {...props} />
}

export function AlertDescription({ className, ...props }: HTMLAttributes<HTMLParagraphElement>) {
  return <div className={cn('text-sm text-muted-foreground', className)} {...props} />
}
