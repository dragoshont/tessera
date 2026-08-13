import { useEffect, useRef, useState } from 'react'
import { AppState } from 'react-native'
import { parseRealtimeProtocolEvent, type RealtimeTurnInput, type RealtimeVoiceStatus } from '@tessera/client'
import { mediaDevices, RTCPeerConnection, RTCSessionDescription, type MediaStream, type MediaStreamTrack } from 'react-native-webrtc'

import type { TesseraApi } from '@/lib/api'

export type NativeVoiceState = 'UNAVAILABLE' | 'IDLE' | 'REQUESTING_PERMISSION' | 'PERMISSION_DENIED' | 'NEGOTIATING' | 'LISTENING' | 'USER_SPEAKING' | 'ASSISTANT_SPEAKING' | 'TOOL_RUNNING' | 'APPROVAL_REQUIRED' | 'INTERRUPTED' | 'SESSION_EXPIRED' | 'ERROR' | 'ENDING'
export type NativeVoiceView = { state: NativeVoiceState; muted?: boolean; blockedCode?: string | null; userCaption?: string; assistantCaption?: string; toolName?: string }

export function useNativeRealtimeVoice({ api, conversationId, status, onTurnSaved, onApprovalRequired }: {
  api: TesseraApi
  conversationId?: string
  status?: RealtimeVoiceStatus
  onTurnSaved?: () => void
  onApprovalRequired?: (actionId: string) => void
}) {
  const [voice, setVoice] = useState<NativeVoiceView>({ state: 'UNAVAILABLE', blockedCode: 'Checking realtime voice…' })
  const peer = useRef<RTCPeerConnection | null>(null)
  const channel = useRef<ReturnType<RTCPeerConnection['createDataChannel']> | null>(null)
  const stream = useRef<MediaStream | null>(null)
  const sessionId = useRef<string | null>(null)
  const activeConversation = useRef<string | undefined>(conversationId)
  const generation = useRef(0)
  const ended = useRef(true)
  const pendingUser = useRef<{ itemId: string; transcript: string } | null>(null)
  const assistantText = useRef('')
  const expiryTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const statusRef = useRef(status)
  const onTurnSavedRef = useRef(onTurnSaved)
  const onApprovalRequiredRef = useRef(onApprovalRequired)
  statusRef.current = status
  onTurnSavedRef.current = onTurnSaved
  onApprovalRequiredRef.current = onApprovalRequired

  const takeTurn = (assistantTranscript: string, outputItemId: string | null, disposition: RealtimeTurnInput['assistantDisposition']) => {
    const user = pendingUser.current
    const currentSession = sessionId.current
    const currentConversation = activeConversation.current
    if (!user || !currentSession || !currentConversation) return null
    pendingUser.current = null
    assistantText.current = ''
    return {
      conversationId: currentConversation,
      sessionId: currentSession,
      input: {
        clientTurnId: crypto.randomUUID(), inputItemId: user.itemId, outputItemId,
        userTranscript: user.transcript, assistantTranscript: assistantTranscript.trim() || null,
        assistantDisposition: disposition,
      } satisfies RealtimeTurnInput,
    }
  }

  const persistCapturedTurn = async (turn: ReturnType<typeof takeTurn>) => {
    if (!turn) return true
    try { await api.saveRealtimeTurn(turn.conversationId, turn.sessionId, turn.input); onTurnSavedRef.current?.(); return true }
    catch { return false }
  }

  const closeLocal = () => {
    generation.current += 1
    if (expiryTimer.current) clearTimeout(expiryTimer.current)
    expiryTimer.current = null
    try { channel.current?.close() } catch { /* already closed */ }
    try { peer.current?.close() } catch { /* already closed */ }
    stream.current?.getTracks().forEach((track: MediaStreamTrack) => track.stop())
    channel.current = null
    peer.current = null
    stream.current = null
    pendingUser.current = null
    assistantText.current = ''
  }

  const end = async (reason: string = 'USER_ENDED') => {
    const currentSession = sessionId.current
    const currentConversation = activeConversation.current
    if (ended.current && !currentSession) return
    const interruptedTurn = takeTurn(assistantText.current, null, 'INTERRUPTED')
    ended.current = true
    setVoice({ state: 'ENDING' })
    closeLocal()
    sessionId.current = null
    void persistCapturedTurn(interruptedTurn)
    if (currentSession && currentConversation) {
      try { await api.endRealtimeVoice(currentConversation, currentSession, reason) } catch { /* local cleanup is authoritative */ }
    }
    const currentStatus = statusRef.current
    setVoice(currentStatus?.state === 'READY' ? { state: 'IDLE' } : { state: 'UNAVAILABLE', blockedCode: currentStatus?.blockedCode ?? 'Realtime voice is unavailable.' })
  }

  const fail = async (code: string, reason: 'ERROR' | 'INTERRUPTED' = 'ERROR') => {
    const currentSession = sessionId.current
    const currentConversation = activeConversation.current
    const failedTurn = takeTurn(assistantText.current, null, 'FAILED')
    ended.current = true
    closeLocal()
    sessionId.current = null
    void persistCapturedTurn(failedTurn)
    setVoice({ state: reason, blockedCode: code })
    if (currentSession && currentConversation) {
      try { await api.endRealtimeVoice(currentConversation, currentSession, reason) } catch { /* local cleanup is authoritative */ }
    }
  }

  const saveTurn = async (assistantTranscript: string, outputItemId: string | null, disposition: RealtimeTurnInput['assistantDisposition']) => {
    const turn = takeTurn(assistantTranscript, outputItemId, disposition)
    if (!await persistCapturedTurn(turn))
      setVoice((current) => ({ ...current, state: 'ERROR', blockedCode: 'realtime_turn_save_failed' }))
  }

  const runTool = async (callId: string, name: string, args: unknown) => {
    const currentSession = sessionId.current
    const currentConversation = activeConversation.current
    if (!currentSession || !currentConversation || !args || typeof args !== 'object' || Array.isArray(args)) return
    const currentGeneration = generation.current
    const toolKey = `rt-${crypto.randomUUID()}`
    setVoice((current) => ({ ...current, state: 'TOOL_RUNNING', toolName: name }))
    try {
      let result = await api.invokeRealtimeTool(currentConversation, currentSession, callId, name, args as Record<string, unknown>, toolKey)
      if (result.state === 'APPROVAL_REQUIRED' && result.actionId) {
        setVoice((current) => ({ ...current, state: 'APPROVAL_REQUIRED', toolName: name }))
        onApprovalRequiredRef.current?.(result.actionId)
        while (result.state === 'APPROVAL_REQUIRED') {
          await new Promise((resolve) => setTimeout(resolve, 1000))
          if (ended.current || generation.current !== currentGeneration) return
          result = await api.invokeRealtimeTool(currentConversation, currentSession, callId, name, args as Record<string, unknown>, toolKey)
        }
      }
      if (channel.current?.readyState === 'open') {
        const output = result.state === 'COMPLETED' && result.output ? result.output : { error: result.errorCode ?? 'tool_failed' }
        channel.current.send(JSON.stringify({ type: 'conversation.item.create', item: { type: 'function_call_output', call_id: callId, output: JSON.stringify(output) } }))
        channel.current.send(JSON.stringify({ type: 'response.create' }))
      }
      setVoice((current) => ({ ...current, state: 'LISTENING', toolName: undefined }))
    } catch { void fail('realtime_tool_failed') }
  }

  const handleData = (raw: string) => {
    const event = parseRealtimeProtocolEvent(raw)
    if (event.type === 'speech_started') {
      if (assistantText.current && pendingUser.current) void saveTurn(assistantText.current, null, 'INTERRUPTED')
      assistantText.current = ''
      setVoice((current) => ({ ...current, state: 'USER_SPEAKING', assistantCaption: undefined }))
    } else if (event.type === 'user_transcript') {
      pendingUser.current = { itemId: event.itemId, transcript: event.transcript }
      setVoice((current) => ({ ...current, state: 'LISTENING', userCaption: event.transcript }))
    } else if (event.type === 'assistant_delta') {
      assistantText.current = `${assistantText.current}${event.delta}`.slice(0, 32 * 1024)
      setVoice((current) => ({ ...current, state: 'ASSISTANT_SPEAKING', assistantCaption: assistantText.current }))
    } else if (event.type === 'assistant_done') {
      const transcript = event.transcript || assistantText.current
      assistantText.current = ''
      setVoice((current) => ({ ...current, state: 'LISTENING', assistantCaption: transcript }))
      void saveTurn(transcript, event.itemId, 'COMPLETED')
    } else if (event.type === 'tool_call') {
      void runTool(event.callId, event.name, event.arguments)
    } else if (event.type === 'provider_error') void fail(event.code)
  }

  const start = async () => {
    if (!conversationId || status?.state !== 'READY' || !ended.current) return
    const currentGeneration = ++generation.current
    ended.current = false
    activeConversation.current = conversationId
    setVoice({ state: 'REQUESTING_PERMISSION' })
    try {
      const localStream = await mediaDevices.getUserMedia({ audio: true, video: false })
      if (generation.current !== currentGeneration) { localStream.getTracks().forEach((track) => track.stop()); return }
      stream.current = localStream
      const connection = new RTCPeerConnection()
      peer.current = connection
      localStream.getTracks().forEach((track) => connection.addTrack(track, localStream))
      const dataChannel = connection.createDataChannel('realtime-channel')
      channel.current = dataChannel
      dataChannel.onopen = () => setVoice((current) => ({ ...current, state: 'LISTENING' }))
      dataChannel.onmessage = (event: { data?: unknown }) => typeof event.data === 'string' && handleData(event.data)
      dataChannel.onerror = () => { void fail('realtime_data_channel_failed') }
      connection.onconnectionstatechange = () => {
        if (connection.connectionState === 'connected') setVoice((current) => ({ ...current, state: 'LISTENING' }))
        if (connection.connectionState === 'failed' || connection.connectionState === 'disconnected') {
          void fail('realtime_connection_interrupted', 'INTERRUPTED')
        }
      }
      setVoice({ state: 'NEGOTIATING' })
      const offer = await connection.createOffer({ offerToReceiveAudio: true })
      await connection.setLocalDescription(offer)
      if (!offer.sdp) throw new Error('realtime_offer_invalid')
      const negotiation = await api.negotiateRealtimeVoice(conversationId, crypto.randomUUID(), offer.sdp)
      if (generation.current !== currentGeneration) return
      sessionId.current = negotiation.sessionId
      await connection.setRemoteDescription(new RTCSessionDescription({ type: 'answer', sdp: negotiation.answerSdp }))
      const expiresIn = Math.max(0, Math.min(negotiation.maxSessionSeconds * 1000, new Date(negotiation.expiresAt).valueOf() - Date.now()))
      expiryTimer.current = setTimeout(() => {
        const expiredSession = sessionId.current
        const interruptedTurn = takeTurn(assistantText.current, null, 'INTERRUPTED')
        ended.current = true
        closeLocal()
        sessionId.current = null
        void persistCapturedTurn(interruptedTurn)
        setVoice({ state: 'SESSION_EXPIRED' })
        if (expiredSession) void api.endRealtimeVoice(conversationId, expiredSession, 'EXPIRED').catch(() => undefined)
      }, expiresIn)
    } catch (cause) {
      const message = cause instanceof Error ? cause.message : 'realtime_start_failed'
      const denied = /permission|not.?allowed|denied/i.test(message)
      if (denied) { closeLocal(); ended.current = true; setVoice({ state: 'PERMISSION_DENIED', blockedCode: message }) }
      else await fail(message)
    }
  }

  const toggleMute = () => {
    const nextMuted = stream.current?.getAudioTracks().some((track) => track.enabled) ?? false
    stream.current?.getAudioTracks().forEach((track) => { track.enabled = !nextMuted })
    setVoice((current) => ({ ...current, muted: nextMuted }))
  }

  const interrupt = () => {
    if (channel.current?.readyState === 'open') {
      channel.current.send(JSON.stringify({ type: 'response.cancel' }))
      channel.current.send(JSON.stringify({ type: 'output_audio_buffer.clear' }))
    }
    setVoice((current) => ({ ...current, state: 'LISTENING', assistantCaption: undefined }))
  }

  useEffect(() => {
    if (!ended.current && activeConversation.current !== conversationId) void end('CONVERSATION_CHANGED')
    activeConversation.current = conversationId
  }, [conversationId])

  useEffect(() => {
    if (ended.current) setVoice(status?.state === 'READY' ? { state: 'IDLE' } : { state: 'UNAVAILABLE', blockedCode: status?.blockedCode ?? (status?.state === 'CHECKING' ? 'Checking realtime voice…' : 'Realtime voice is unavailable.') })
  }, [status])

  useEffect(() => {
    const endCurrent = (reason: string) => { void end(reason) }
    const subscription = AppState.addEventListener('change', (next) => {
      if (next !== 'active' && !ended.current) endCurrent('APP_BACKGROUNDED')
    })
    return () => { subscription.remove(); endCurrent('APP_BACKGROUNDED') }
  }, [])

  return { voice, start, retry: start, toggleMute, interrupt, end }
}
