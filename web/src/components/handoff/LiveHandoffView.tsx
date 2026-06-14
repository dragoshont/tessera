import type { LiveViewHandle, LiveViewResult } from '../../data/types'
import { LiveHandoffStage } from './LiveHandoffStage'
import { useHandoff, type VerifyResult } from './useHandoff'
import type { HandoffStatus } from './handoff-machine'

export interface LiveHandoffViewProps {
  connectionId: string
  provider: string
  ownerPrincipal: string
  /** Mint a handle (the broker client, or an in-memory seed in stories/tests). */
  requestLiveView: (connectionId: string) => Promise<LiveViewResult>
  verify?: () => Promise<VerifyResult>
  initialStatus?: HandoffStatus
  autoStart?: boolean
  /** Leave the stage (Cancel / Back to My accounts). */
  onExit?: () => void
  /** Pop the worker session out to its own tab; defaults to window.open. */
  onPopOut?: (handle: LiveViewHandle) => void
  onGetHelp?: () => void
}

function popOutDefault(handle: LiveViewHandle) {
  // Open the worker session in a tab (spec "Pop out"); the URL is handed to the
  // browser, never displayed as text.
  window.open(handle.liveViewUrl, '_blank', 'noopener,noreferrer')
}

/**
 * The Live hand-off stage wired to its orchestration hook. Splitting this from
 * the router-bound page keeps it trivially storyable/testable: feed it any
 * `requestLiveView` (an in-memory client seed) and it drives the full machine.
 */
export function LiveHandoffView({
  connectionId,
  provider,
  ownerPrincipal,
  requestLiveView,
  verify,
  initialStatus,
  autoStart,
  onExit,
  onPopOut = popOutDefault,
  onGetHelp,
}: LiveHandoffViewProps) {
  const handoff = useHandoff({ connectionId, requestLiveView, verify, initialStatus, autoStart })
  const { state } = handoff

  return (
    <LiveHandoffStage
      status={state.status}
      handle={state.handle}
      provider={provider}
      ownerPrincipal={ownerPrincipal}
      hostname={state.handle?.targetHostname}
      unavailableReason={state.unavailableReason}
      errorReason={state.errorReason}
      onStart={handoff.start}
      onCancel={onExit}
      onManualDone={handoff.markDone}
      onRestart={handoff.restart}
      onPopOut={() => {
        if (state.handle) onPopOut(state.handle)
      }}
      onBackToAccounts={onExit}
      onGetHelp={onGetHelp}
      onIframeReady={handoff.onIframeReady}
      onSessionEvent={handoff.onSessionEvent}
      onExpire={handoff.onExpire}
    />
  )
}
