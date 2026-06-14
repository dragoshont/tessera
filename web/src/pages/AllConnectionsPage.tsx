import { Lock } from 'lucide-react'
import { Button } from '../components/ui/button'
import { Card, CardContent } from '../components/ui/card'

// Step-up gate placeholder. The real All-connections surface is a separate,
// extra-audited route entered only after step-up re-auth (later phase).
export function AllConnectionsPage() {
  return (
    <div className="flex min-h-[60vh] items-center justify-center">
      <Card className="w-full max-w-md">
        <CardContent className="flex flex-col items-center gap-4 p-8 text-center">
          <span className="flex h-12 w-12 items-center justify-center rounded-2xl bg-muted">
            <Lock className="h-6 w-6 text-muted-foreground" aria-hidden />
          </span>
          <div className="space-y-1">
            <h1 className="text-lg font-semibold">Admin area</h1>
            <p className="text-sm text-muted-foreground">
              All connections shows every person's accounts. Confirm it's you to continue. This visit
              is audited.
            </p>
          </div>
          <Button disabled title="Step-up sign-in lands in a later phase">
            Continue with Microsoft
          </Button>
          <p className="text-xs text-muted-foreground">Step-up sign-in arrives in a later phase.</p>
        </CardContent>
      </Card>
    </div>
  )
}
