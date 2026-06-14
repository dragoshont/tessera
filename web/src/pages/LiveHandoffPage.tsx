import { useCallback } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { ArrowLeft } from 'lucide-react'
import type { LiveViewResult } from '../data/types'
import { demoLiveViewHandle } from '../data/fixtures'
import { useConnection, useTesseraClient } from '../api/hooks'
import { LiveHandoffView } from '../components/handoff/LiveHandoffView'
import { Button } from '../components/ui/button'

// Recover provider/owner from the broker connection id ("{provider}:{principal}")
// as a fallback when the connection projection hasn't loaded yet.
function providerFromConnectionId(connectionId: string): string {
  const separator = connectionId.indexOf(':')
  return separator > 0 ? connectionId.slice(0, separator) : connectionId
}

function principalFromConnectionId(connectionId: string): string | undefined {
  const separator = connectionId.indexOf(':')
  return separator >= 0 ? connectionId.slice(separator + 1) : undefined
}

/**
 * The Live hand-off route (`/handoff/:connectionId`). Wires the broker client and
 * route params to the stage. With `?demo=1` it swaps in a local demo handle so the
 * stage is demoable on fixtures (the real worker is a labeled backend gap); the
 * honest default with no backend is the fail-closed Unavailable state.
 */
export function LiveHandoffPage() {
  const { connectionId: rawConnectionId } = useParams()
  const connectionId = rawConnectionId ? decodeURIComponent(rawConnectionId) : ''
  const [searchParams] = useSearchParams()
  const demo = searchParams.get('demo') === '1'
  const navigate = useNavigate()
  const client = useTesseraClient()
  const { data: connection } = useConnection(connectionId)

  const provider = connection?.provider ?? providerFromConnectionId(connectionId)
  const ownerPrincipal = connection?.ownerPrincipal ?? principalFromConnectionId(connectionId) ?? ''

  const requestLiveView = useCallback(
    (id: string): Promise<LiveViewResult> =>
      demo ? Promise.resolve({ handle: demoLiveViewHandle }) : client.requestLiveView(id),
    [client, demo],
  )

  return (
    <section className="flex flex-col gap-4">
      <div className="flex flex-col gap-1">
        <Button
          variant="ghost"
          size="sm"
          className="-ml-2 self-start text-muted-foreground"
          onClick={() => navigate('/accounts')}
        >
          <ArrowLeft className="h-4 w-4" aria-hidden />
          My accounts
        </Button>
        <h1 className="text-xl font-semibold tracking-tight">Live hand-off</h1>
      </div>

      <LiveHandoffView
        connectionId={connectionId}
        provider={provider}
        ownerPrincipal={ownerPrincipal}
        requestLiveView={requestLiveView}
        onExit={() => navigate('/accounts')}
      />
    </section>
  )
}
