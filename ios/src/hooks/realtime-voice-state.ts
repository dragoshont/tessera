export type RealtimeStartFailure = {
  state: 'PERMISSION_DENIED' | 'ERROR'
  code: 'realtime_permission_denied' | 'realtime_start_failed'
}

export const MaximumRealtimeCaptionCharacters = 32_000

export function boundedRealtimeCaption(value: string): string {
  return value.slice(0, MaximumRealtimeCaptionCharacters)
}

export function classifyRealtimeStartFailure(cause: unknown): RealtimeStartFailure {
  const message = cause instanceof Error ? cause.message : ''
  if (/permission|not.?allowed|denied/i.test(message)) {
    return { state: 'PERMISSION_DENIED', code: 'realtime_permission_denied' }
  }
  return { state: 'ERROR', code: 'realtime_start_failed' }
}

export async function handoffVoiceToAction(endVoice: () => Promise<void>, navigate: () => void): Promise<void> {
  await endVoice()
  navigate()
}