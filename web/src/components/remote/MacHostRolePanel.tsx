import { Laptop, Power } from 'lucide-react'
import type { MacHostStatus } from '../../app/runtime'
import { Alert, AlertDescription, AlertTitle } from '../ui/alert'
import { Button } from '../ui/button'

function macHostStatusLabel(status: MacHostStatus): string {
  switch (status.state) {
  case 'ENABLED': return 'Enabled'
  case 'REQUIRES_APPROVAL': return 'Approval required in System Settings'
  case 'DISABLED': case 'NOT_FOUND': return 'Available, not enabled'
  case 'CLIENT_ONLY': return 'Client only'
  default: return 'Unavailable'
  }
}

export function MacHostRolePanel({
  status,
  checking = false,
  busy = false,
  error,
  onSetEnabled,
}: {
  status?: MacHostStatus
  checking?: boolean
  busy?: boolean
  error?: string | null
  onSetEnabled?: (enabled: boolean) => void
}) {
  const enabled = status?.state === 'ENABLED'
  return (
    <Alert>
      <Laptop aria-hidden />
      <AlertTitle>Mac Host mode</AlertTitle>
      <AlertDescription className="mt-1">
        {checking ? 'Checking the packaged helper…' : status ? `${macHostStatusLabel(status)}.` : 'Helper status could not be read.'}
        {' '}The Electron renderer receives status and this enable intent only; native keys and repository paths remain helper-owned.
      </AlertDescription>
      {error ? <p role="alert" className="mt-2 text-sm text-health-error">{error}</p> : null}
      {status?.available ? (
        <Button className="mt-3 min-h-11" variant={enabled ? 'outline' : 'default'} disabled={busy || status.state === 'REQUIRES_APPROVAL'} onClick={() => onSetEnabled?.(!enabled)}>
          {enabled ? <Power aria-hidden /> : <Laptop aria-hidden />}{busy ? 'Updating…' : enabled ? 'Disable Host mode' : 'Enable Host mode'}
        </Button>
      ) : null}
    </Alert>
  )
}