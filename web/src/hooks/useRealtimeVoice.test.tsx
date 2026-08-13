import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { useRealtimeVoice, type BrowserVoiceDependencies } from './useRealtimeVoice'

class FakeTrack {
  enabled = true
  stopped = false
  stop() { this.stopped = true }
}

class FakeStream {
  readonly track = new FakeTrack()
  getTracks() { return [this.track] as unknown as MediaStreamTrack[] }
  getAudioTracks() { return [this.track] as unknown as MediaStreamTrack[] }
}

class FakeChannel {
  readyState: RTCDataChannelState = 'open'
  onopen: (() => void) | null = null
  onmessage: ((event: MessageEvent) => void) | null = null
  onerror: (() => void) | null = null
  sent: string[] = []
  closed = false
  send(value: string) { this.sent.push(value) }
  close() { this.closed = true; this.readyState = 'closed' }
  emit(value: unknown) { this.onmessage?.(new MessageEvent('message', { data: JSON.stringify(value) })) }
}

class FakePeer {
  connectionState: RTCPeerConnectionState = 'new'
  onconnectionstatechange: (() => void) | null = null
  ontrack: ((event: RTCTrackEvent) => void) | null = null
  channel = new FakeChannel()
  local?: RTCSessionDescriptionInit
  remote?: RTCSessionDescriptionInit
  closed = false
  addTrack() { return {} as RTCRtpSender }
  createDataChannel() { return this.channel as unknown as RTCDataChannel }
  async createOffer() { return { type: 'offer' as const, sdp: 'v=0\r\nm=audio 9 RTP/AVP 111\r\n' } }
  async setLocalDescription(value: RTCSessionDescriptionInit) { this.local = value }
  async setRemoteDescription(value: RTCSessionDescriptionInit) { this.remote = value }
  close() { this.closed = true; this.connectionState = 'closed' }
}

class FakeAudio {
  srcObject: MediaProvider | null = null
  autoplay = true
  style = { display: '' }
  removed = false
  async play() { return undefined }
  remove() { this.removed = true }
}

const readyStatus = { state: 'READY' as const, blockedCode: null, supportsTools: false, maxSessionSeconds: 900, checkedAt: '2026-08-13T00:00:00Z', validUntil: '2026-08-13T00:05:00Z', version: 1 }

function setup(expiresAt = new Date(Date.now() + 15 * 60 * 1000).toISOString()) {
  const stream = new FakeStream()
  const peer = new FakePeer()
  const audio = new FakeAudio()
  const order: string[] = []
  const api = {
    negotiateRealtimeVoice: vi.fn(async () => { order.push('negotiate'); return { sessionId: 'session-1', answerSdp: 'v=0\r\nm=audio 9 RTP/AVP 111\r\n', negotiatedAt: '2026-08-13T00:00:00Z', expiresAt, maxSessionSeconds: 900 } }),
    saveRealtimeTurn: vi.fn(async () => ({ sessionId: 'session-1', clientTurnId: 'id-2', replayed: false })),
    invokeRealtimeTool: vi.fn(async () => ({ sessionId: 'session-1', clientCallId: 'call-1', state: 'COMPLETED' as const, capabilityCallId: 'cap-1', capabilityResultId: 'result-1', actionId: null, output: { time: '12:00' }, errorCode: null })),
    endRealtimeVoice: vi.fn(async () => ({ id: 'session-1', resourceType: 'realtime_session', version: 2 })),
  }
  let id = 0
  const dependencies: BrowserVoiceDependencies = {
    api,
    getUserMedia: vi.fn(async () => { order.push('permission'); return stream as unknown as MediaStream }),
    createPeerConnection: () => peer as unknown as RTCPeerConnection,
    createAudio: () => audio as unknown as HTMLAudioElement,
    createId: () => `id-${++id}`,
    wait: async () => undefined,
  }
  return { stream, peer, audio, api, dependencies, order }
}

describe('useRealtimeVoice', () => {
  afterEach(() => vi.useRealTimers())
  it('requests permission before SDP negotiation and applies the server answer', async () => {
    const test = setup()
    const { result } = renderHook(() => useRealtimeVoice({ conversationId: 'conversation-1', status: readyStatus, dependencies: test.dependencies }))
    await act(() => result.current.start())
    expect(test.order).toEqual(['permission', 'negotiate'])
    expect(test.peer.local?.type).toBe('offer')
    expect(test.peer.remote?.type).toBe('answer')
    act(() => test.peer.channel.onopen?.())
    expect(result.current.voice.state).toBe('LISTENING')
  })

  it('persists completed captions to the canonical conversation', async () => {
    const test = setup()
    const saved = vi.fn()
    const { result } = renderHook(() => useRealtimeVoice({ conversationId: 'conversation-1', status: readyStatus, dependencies: test.dependencies, onTurnSaved: saved }))
    await act(() => result.current.start())
    act(() => {
      test.peer.channel.emit({ type: 'conversation.item.input_audio_transcription.completed', item_id: 'input-1', transcript: 'Hello' })
      test.peer.channel.emit({ type: 'response.output_audio_transcript.delta', item_id: 'output-1', delta: 'Hi ' })
      test.peer.channel.emit({ type: 'response.output_audio_transcript.done', item_id: 'output-1', transcript: 'Hi there' })
    })
    await waitFor(() => expect(test.api.saveRealtimeTurn).toHaveBeenCalledOnce())
    expect(test.api.saveRealtimeTurn).toHaveBeenCalledWith('conversation-1', 'session-1', expect.objectContaining({ inputItemId: 'input-1', outputItemId: 'output-1', userTranscript: 'Hello', assistantTranscript: 'Hi there', assistantDisposition: 'COMPLETED' }))
    expect(saved).toHaveBeenCalledOnce()
  })

  it('mutes the real audio track and closes local media before ending server metadata', async () => {
    const test = setup()
    const { result } = renderHook(() => useRealtimeVoice({ conversationId: 'conversation-1', status: readyStatus, dependencies: test.dependencies }))
    await act(() => result.current.start())
    act(() => result.current.toggleMute())
    expect(test.stream.track.enabled).toBe(false)
    await act(() => result.current.end())
    expect(test.stream.track.stopped).toBe(true)
    expect(test.peer.closed).toBe(true)
    expect(test.peer.channel.closed).toBe(true)
    expect(test.audio.removed).toBe(true)
    expect(test.api.endRealtimeVoice).toHaveBeenCalledWith('conversation-1', 'session-1', 'USER_ENDED')
  })

  it('closes media before reporting a provider data-channel error', async () => {
    const test = setup()
    const { result } = renderHook(() => useRealtimeVoice({ conversationId: 'conversation-1', status: readyStatus, dependencies: test.dependencies }))
    await act(() => result.current.start())
    act(() => test.peer.channel.emit({ type: 'error', error: { code: 'provider_failed' } }))
    await waitFor(() => expect(result.current.voice.state).toBe('ERROR'))
    expect(test.stream.track.stopped).toBe(true)
    expect(test.peer.closed).toBe(true)
    expect(test.api.endRealtimeVoice).toHaveBeenCalledWith('conversation-1', 'session-1', 'ERROR')
  })

  it('expires locally, closes media, and requires explicit restart', async () => {
    vi.useFakeTimers()
    const test = setup(new Date(Date.now() + 1000).toISOString())
    const { result } = renderHook(() => useRealtimeVoice({ conversationId: 'conversation-1', status: readyStatus, dependencies: test.dependencies }))
    await act(() => result.current.start())
    await act(async () => { vi.advanceTimersByTime(1000); await Promise.resolve() })
    expect(result.current.voice.state).toBe('SESSION_EXPIRED')
    expect(test.stream.track.stopped).toBe(true)
    expect(test.api.endRealtimeVoice).toHaveBeenCalledWith('conversation-1', 'session-1', 'EXPIRED')
  })

  it('relays only canonical completed tool output back to Foundry', async () => {
    const test = setup()
    const { result } = renderHook(() => useRealtimeVoice({ conversationId: 'conversation-1', status: readyStatus, dependencies: test.dependencies }))
    await act(() => result.current.start())
    act(() => test.peer.channel.emit({ type: 'response.output_item.done', item: { type: 'function_call', call_id: 'call-1', name: 'current_time', arguments: '{"timeZone":"UTC"}' } }))
    await waitFor(() => expect(test.api.invokeRealtimeTool).toHaveBeenCalledOnce())
    expect(test.peer.channel.sent.map((value) => JSON.parse(value))).toEqual([
      { type: 'conversation.item.create', item: { type: 'function_call_output', call_id: 'call-1', output: '{"time":"12:00"}' } },
      { type: 'response.create' },
    ])
  })

  it('pauses consequential tool continuation for canonical Action approval', async () => {
    const test = setup()
    test.api.invokeRealtimeTool.mockResolvedValueOnce({ sessionId: 'session-1', clientCallId: 'call-1', state: 'APPROVAL_REQUIRED', capabilityCallId: 'cap-1', capabilityResultId: null, actionId: 'action-1', output: null, errorCode: null })
    let releaseApproval!: () => void
    test.dependencies.wait = () => new Promise<void>((resolve) => { releaseApproval = resolve })
    const approval = vi.fn()
    const { result } = renderHook(() => useRealtimeVoice({ conversationId: 'conversation-1', status: readyStatus, dependencies: test.dependencies, onApprovalRequired: approval }))
    await act(() => result.current.start())
    act(() => test.peer.channel.emit({ type: 'response.output_item.done', item: { type: 'function_call', call_id: 'call-1', name: 'remember_memory', arguments: '{"value":"yes"}' } }))
    await waitFor(() => expect(result.current.voice.state).toBe('APPROVAL_REQUIRED'))
    expect(approval).toHaveBeenCalledWith('action-1')
    expect(test.peer.channel.sent).toEqual([])
    await act(async () => { releaseApproval(); await Promise.resolve() })
    await waitFor(() => expect(test.peer.channel.sent).toHaveLength(2))
    expect(test.api.invokeRealtimeTool).toHaveBeenCalledTimes(2)
  })

  it('stops media before best-effort persistence of an interrupted partial turn', async () => {
    const test = setup()
    let trackStoppedAtSave = false
    test.api.saveRealtimeTurn.mockImplementationOnce(async () => {
      trackStoppedAtSave = test.stream.track.stopped
      return { sessionId: 'session-1', clientTurnId: 'turn-interrupted', replayed: false }
    })
    const { result } = renderHook(() => useRealtimeVoice({ conversationId: 'conversation-1', status: readyStatus, dependencies: test.dependencies }))
    await act(() => result.current.start())
    act(() => {
      test.peer.channel.emit({ type: 'conversation.item.input_audio_transcription.completed', item_id: 'input-1', transcript: 'Keep this' })
      test.peer.channel.emit({ type: 'response.output_audio_transcript.delta', item_id: 'output-1', delta: 'Partial answer' })
    })
    await act(() => result.current.end())
    await waitFor(() => expect(test.api.saveRealtimeTurn).toHaveBeenCalledOnce())
    expect(trackStoppedAtSave).toBe(true)
    expect(test.api.saveRealtimeTurn).toHaveBeenCalledWith('conversation-1', 'session-1', expect.objectContaining({ assistantDisposition: 'INTERRUPTED', userTranscript: 'Keep this', assistantTranscript: 'Partial answer' }))
  })
})
