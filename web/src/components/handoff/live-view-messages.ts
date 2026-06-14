// The postMessage event contract with the worker (spec C.2 + backend gap note).
// Names must be agreed with the worker before this ships against a real backend.
// Kept separate from the component so the live canvas file only exports a component
// (react-refresh).
export const LIVE_VIEW_MESSAGES = [
  'tessera-session-ready',
  'tessera-session-done',
  'tessera-session-disconnected',
  'tessera-session-expired',
  'tessera-session-error',
] as const

export type LiveViewMessage = (typeof LIVE_VIEW_MESSAGES)[number]

function isLiveViewMessage(value: unknown): value is LiveViewMessage {
  return typeof value === 'string' && (LIVE_VIEW_MESSAGES as readonly string[]).includes(value)
}

/** Accept either a bare string (`"tessera-session-done"`) or `{ type: "…" }`. */
export function parseLiveViewMessage(data: unknown): LiveViewMessage | null {
  if (isLiveViewMessage(data)) return data
  if (data && typeof data === 'object' && 'type' in data) {
    const type = (data as { type?: unknown }).type
    if (isLiveViewMessage(type)) return type
  }
  return null
}
