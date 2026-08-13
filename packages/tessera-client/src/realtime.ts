export type RealtimeVoiceStatus = {
  state: 'CHECKING' | 'READY' | 'BLOCKED' | 'UNAVAILABLE'
  blockedCode: string | null
  supportsTools: boolean
  maxSessionSeconds: number
  checkedAt: string | null
  validUntil: string | null
  version: number
}

export type RealtimeNegotiation = {
  sessionId: string
  answerSdp: string
  negotiatedAt: string
  expiresAt: string
  maxSessionSeconds: number
}

export type RealtimeTurnInput = {
  clientTurnId: string
  inputItemId: string
  outputItemId: string | null
  userTranscript: string
  assistantTranscript: string | null
  assistantDisposition: 'COMPLETED' | 'INTERRUPTED' | 'FAILED'
}

export type RealtimeTurnReceipt = {
  sessionId: string
  clientTurnId: string
  replayed: boolean
}

export type RealtimeToolCallResult = {
  sessionId: string
  clientCallId: string
  state: 'COMPLETED' | 'APPROVAL_REQUIRED' | 'FAILED'
  capabilityCallId: string | null
  capabilityResultId: string | null
  actionId: string | null
  output: Record<string, unknown> | null
  errorCode: string | null
}

export type RealtimeProtocolEvent =
  | { type: 'speech_started' }
  | { type: 'user_transcript'; itemId: string; transcript: string }
  | { type: 'assistant_delta'; itemId: string | null; delta: string }
  | { type: 'assistant_done'; itemId: string | null; transcript: string }
  | { type: 'tool_call'; callId: string; name: string; arguments: unknown }
  | { type: 'provider_error'; code: string }
  | { type: 'ignored' }

const MAXIMUM_EVENT_BYTES = 256 * 1024
const identifier = (value: unknown) => typeof value === 'string' && /^[!-~]{1,128}$/.test(value) ? value : null
const text = (value: unknown, maximum: number) => typeof value === 'string' && value.length <= maximum ? value : null

export function parseRealtimeProtocolEvent(raw: string): RealtimeProtocolEvent {
  if (new TextEncoder().encode(raw).length > MAXIMUM_EVENT_BYTES) return { type: 'provider_error', code: 'provider_event_too_large' }
  let value: unknown
  try { value = JSON.parse(raw) } catch { return { type: 'ignored' } }
  if (!value || typeof value !== 'object' || Array.isArray(value)) return { type: 'ignored' }
  const event = value as Record<string, unknown>
  const type = event.type
  if (type === 'input_audio_buffer.speech_started') return { type: 'speech_started' }
  if (type === 'conversation.item.input_audio_transcription.completed') {
    const itemId = identifier(event.item_id)
    const transcript = text(event.transcript, 32 * 1024)
    return itemId && transcript ? { type: 'user_transcript', itemId, transcript } : { type: 'provider_error', code: 'provider_event_malformed' }
  }
  if (type === 'response.output_audio_transcript.delta') {
    const delta = text(event.delta, 16 * 1024)
    return delta === null ? { type: 'provider_error', code: 'provider_event_malformed' } : { type: 'assistant_delta', itemId: identifier(event.item_id), delta }
  }
  if (type === 'response.output_audio_transcript.done') {
    const transcript = text(event.transcript, 32 * 1024)
    return transcript === null ? { type: 'provider_error', code: 'provider_event_malformed' } : { type: 'assistant_done', itemId: identifier(event.item_id), transcript }
  }
  if (type === 'response.output_item.done' || type === 'response.function_call_arguments.done') {
    const item = event.item && typeof event.item === 'object' && !Array.isArray(event.item) ? event.item as Record<string, unknown> : event
    if ((item.type === 'function_call' || type === 'response.function_call_arguments.done')) {
      const callId = identifier(item.call_id)
      const name = identifier(item.name)
      if (!callId || !name) return { type: 'provider_error', code: 'provider_event_malformed' }
      let args: unknown = item.arguments ?? {}
      if (typeof args === 'string') { try { args = JSON.parse(args) } catch { return { type: 'provider_error', code: 'provider_event_malformed' } } }
      if (!args || typeof args !== 'object' || Array.isArray(args)) return { type: 'provider_error', code: 'provider_event_malformed' }
      return { type: 'tool_call', callId, name, arguments: args }
    }
  }
  if (type === 'error') {
    const error = event.error && typeof event.error === 'object' && !Array.isArray(event.error) ? event.error as Record<string, unknown> : {}
    return { type: 'provider_error', code: identifier(error.code) ?? 'provider_realtime_error' }
  }
  return { type: 'ignored' }
}