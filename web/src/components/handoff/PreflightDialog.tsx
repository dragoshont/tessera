import { Button } from '../ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '../ui/dialog'

export interface PreflightDialogProps {
  open: boolean
  provider: string
  onStart: () => void
  onCancel: () => void
}

/**
 * The ~2-minute pre-flight (spec §6 / copy deck). Nothing happens until the person
 * accepts here — the broker is only asked to mint a handle on [Start], never on
 * page load. The trust promise is stated up front: we keep the session, never the
 * password.
 */
export function PreflightDialog({ open, provider, onStart, onCancel }: PreflightDialogProps) {
  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        if (!next) onCancel()
      }}
    >
      <DialogContent className="max-w-md" showClose={false}>
        <DialogHeader>
          <DialogTitle>Connect {provider}</DialogTitle>
          <DialogDescription>This takes about 2 minutes.</DialogDescription>
        </DialogHeader>
        <ul className="flex flex-col gap-2 text-sm text-foreground">
          <li className="flex gap-2">
            <span aria-hidden className="text-muted-foreground">
              •
            </span>
            You'll log in to {provider} and solve the checkbox.
          </li>
          <li className="flex gap-2">
            <span aria-hidden className="text-muted-foreground">
              •
            </span>
            We keep the logged-in session — we never see your password.
          </li>
          <li className="flex gap-2">
            <span aria-hidden className="text-muted-foreground">
              •
            </span>
            If it times out, you can start again.
          </li>
        </ul>
        <DialogFooter>
          <Button variant="outline" onClick={onCancel}>
            Not now
          </Button>
          <Button onClick={onStart}>Start</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
