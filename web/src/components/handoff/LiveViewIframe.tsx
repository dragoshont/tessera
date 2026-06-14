import { useEffect } from 'react'
import type { LiveViewHandle } from '../../data/types'
import { cn } from '../../lib/utils'
import { parseLiveViewMessage, type LiveViewMessage } from './live-view-messages'

export type { LiveViewMessage } from './live-view-messages'

export interface LiveViewIframeProps {
  handle: LiveViewHandle
  /** Drive the state machine from worker postMessage events. */
  onMessage: (message: LiveViewMessage) => void
  /** Reliable readiness even when the worker can't post `tessera-session-ready`
   *  (R2: liveness is best-effort per recipe) — the iframe load is the fallback. */
  onReady?: () => void
  className?: string
}

/**
 * The live canvas: the worker browser embedded in Tessera chrome. The raw
 * `handle.liveViewUrl` is used ONLY as the iframe `src` — it is never rendered as
 * text or linked (anti-pattern #4). `allow-same-origin allow-scripts` is required
 * for the trusted worker's interactive session; clipboard is allowed for paste
 * during login.
 */
export function LiveViewIframe({ handle, onMessage, onReady, className }: LiveViewIframeProps) {
  useEffect(() => {
    function handleWindowMessage(event: MessageEvent) {
      const message = parseLiveViewMessage(event.data)
      if (message) onMessage(message)
    }
    window.addEventListener('message', handleWindowMessage)
    return () => window.removeEventListener('message', handleWindowMessage)
  }, [onMessage])

  return (
    <iframe
      // A generic title — never the worker URL.
      title="Live remote browser"
      src={handle.liveViewUrl}
      sandbox="allow-same-origin allow-scripts"
      allow="clipboard-read; clipboard-write"
      className={cn('h-full w-full border-0 bg-muted', className)}
      onLoad={() => onReady?.()}
    />
  )
}
