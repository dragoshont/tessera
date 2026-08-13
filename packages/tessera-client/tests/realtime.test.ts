import { describe, expect, it } from 'vitest'
import { parseRealtimeProtocolEvent } from '../src/realtime'

describe('parseRealtimeProtocolEvent', () => {
  it('maps completed user and assistant transcripts without exposing provider bodies', () => {
    expect(parseRealtimeProtocolEvent(JSON.stringify({ type: 'conversation.item.input_audio_transcription.completed', item_id: 'input-1', transcript: 'Hello' })))
      .toEqual({ type: 'user_transcript', itemId: 'input-1', transcript: 'Hello' })
    expect(parseRealtimeProtocolEvent(JSON.stringify({ type: 'response.output_audio_transcript.done', item_id: 'output-1', transcript: 'Hi' })))
      .toEqual({ type: 'assistant_done', itemId: 'output-1', transcript: 'Hi' })
  })

  it('maps bounded tool calls and rejects malformed arguments', () => {
    expect(parseRealtimeProtocolEvent(JSON.stringify({ type: 'response.output_item.done', item: { type: 'function_call', call_id: 'call-1', name: 'tool.one', arguments: '{"value":1}' } })))
      .toEqual({ type: 'tool_call', callId: 'call-1', name: 'tool.one', arguments: { value: 1 } })
    expect(parseRealtimeProtocolEvent(JSON.stringify({ type: 'response.function_call_arguments.done', call_id: 'call-2', name: 'tool.two', arguments: 'not-json' })))
      .toEqual({ type: 'provider_error', code: 'provider_event_malformed' })
  })

  it('ignores unknown events and fails bounded oversized events', () => {
    expect(parseRealtimeProtocolEvent('{"type":"session.created"}')).toEqual({ type: 'ignored' })
    expect(parseRealtimeProtocolEvent(JSON.stringify({ type: 'unknown', value: 'x'.repeat(256 * 1024) })))
      .toEqual({ type: 'provider_error', code: 'provider_event_too_large' })
  })
})