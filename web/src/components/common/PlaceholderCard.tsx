import type { ReactNode } from 'react'
import { Card, CardContent } from '../ui/card'

export function PlaceholderCard({
  title,
  body,
  action,
}: {
  title: string
  body: string
  action?: ReactNode
}) {
  return (
    <Card>
      <CardContent className="flex flex-col items-start gap-3 p-6">
        <h2 className="text-base font-semibold">{title}</h2>
        <p className="text-sm text-muted-foreground">{body}</p>
        {action}
      </CardContent>
    </Card>
  )
}
