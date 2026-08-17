export type StatusTone = 'success' | 'warning' | 'danger' | 'neutral'

export function statusTone(value: string): StatusTone {
  const state = value.toUpperCase()
  if (['FAIL', 'ERROR', 'REVOK', 'OFFLINE', 'DENIED', 'RESTRICTED', 'EXPIRED', 'CANCEL', 'INVALID', 'BLOCKED', 'UNAVAILABLE', 'INTERRUPTED'].some((part) => state.includes(part))) return 'danger'
  if (['WAIT', 'DEGRA', 'PENDING', 'PROPOSED', 'APPROVAL', 'RECONCILIATION', 'REQUESTING', 'NEGOTIATING', 'STARTING', 'PROCESSING', 'UNDETERMINED', 'NOT_DETERMINED', 'CHECKING', 'PAUSED'].some((part) => state.includes(part))) return 'warning'
  if (['READY', 'HEALTHY', 'CONNECTED', 'ACTIVE', 'COMPLETED', 'SUCCEEDED', 'GRANTED', 'AUTHORIZED', 'PROVISIONAL', 'EPHEMERAL', 'ENABLED', 'IDLE', 'LISTENING'].some((part) => state.includes(part))) return 'success'
  return 'neutral'
}