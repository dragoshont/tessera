import { useCallback, useEffect, useRef, useState } from 'react'
import { parseRealtimeProtocolEvent, type RealtimeTurnInput, type RealtimeVoiceStatus } from '@tessera/client'
import type { RealtimeVoiceView } from '../components/chat/ChatWorkspace'
import { r2Api } from '../api/r2'

type RealtimeApi = Pick<typeof r2Api, 'negotiateRealtimeVoice' | 'saveRealtimeTurn' | 'invokeRealtimeTool' | 'endRealtimeVoice'>

export type BrowserVoiceDependencies = {
  api: RealtimeApi
  getUserMedia: () => Promise<MediaStream>
  createPeerConnection: () => RTCPeerConnection
  createAudio: () => HTMLAudioElement
  createId: () => string
  wait: (milliseconds: number) => Promise<void>
}

const browserDependencies: BrowserVoiceDependencies = {
  api: r2Api,
  getUserMedia: () => navigator.mediaDevices.getUserMedia({ audio: true, video: false }),
  createPeerConnection: () => new RTCPeerConnection(),
  createAudio: () => {
    const audio = document.createElement('audio')
    audio.autoplay = true
    audio.style.display = 'none'
    document.body.append(audio)
    return audio
  },
  createId: () => crypto.randomUUID(),
  wait: (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds)),
}

export function useRealtimeVoice({
  conversationId,
  status,
  onTurnSaved,
  onApprovalRequired,
  dependencies = browserDependencies,
}: {
  conversationId?: string
  status?: RealtimeVoiceStatus
  onTurnSaved?: () => void
  onApprovalRequired?: (actionId: string) => void
  dependencies?: BrowserVoiceDependencies
}) {
  const [voice, setVoice] = useState<RealtimeVoiceView>({ state: 'UNAVAILABLE', blockedCode: 'Checking realtime voice…' })
  const peer = useRef<RTCPeerConnection | null>(null)
  const channel = useRef<RTCDataChannel | null>(null)
  const stream = useRef<MediaStream | null>(null)
  const audio = useRef<HTMLAudioElement | null>(null)
  const sessionId = useRef<string | null>(null)
  const activeConversation = useRef<string | undefined>(conversationId)
  const generation = useRef(0)
  const pendingUser = useRef<{ itemId: string; transcript: string } | null>(null)
  const assistantText = useRef('')
  const ended = useRef(true)
  const expiryTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const onTurnSavedRef = useRef(onTurnSaved)
  const onApprovalRequiredRef = useRef(onApprovalRequired)
  const statusRef = useRef(status)
  onTurnSavedRef.current = onTurnSaved
  onApprovalRequiredRef.current = onApprovalRequired
  statusRef.current = status

  const takeTurn = useCallback((assistantTranscript: string, outputItemId: string | null, disposition: RealtimeTurnInput['assistantDisposition']) => {
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
        clientTurnId: dependencies.createId(), inputItemId: user.itemId, outputItemId,
        userTranscript: user.transcript, assistantTranscript: assistantTranscript.trim() || null,
        assistantDisposition: disposition,
      } satisfies RealtimeTurnInput,
    }
  }, [dependencies])

  const persistCapturedTurn = useCallback(async (turn: ReturnType<typeof takeTurn>) => {
    if (!turn) return true
    try { await dependencies.api.saveRealtimeTurn(turn.conversationId, turn.sessionId, turn.input); onTurnSavedRef.current?.(); return true }
    catch { return false }
  }, [dependencies.api, takeTurn])

  const closeLocal = useCallback(() => {
    generation.current += 1
    if (expiryTimer.current) clearTimeout(expiryTimer.current)
    expiryTimer.current = null
    try { channel.current?.close() } catch { /* already closed */ }
    try { peer.current?.close() } catch { /* already closed */ }
    stream.current?.getTracks().forEach((track) => track.stop())
    if (audio.current) {
      audio.current.srcObject = null
      audio.current.remove()
    }
    channel.current = null
    peer.current = null
    stream.current = null
    audio.current = null
    pendingUser.current = null
    assistantText.current = ''
  }, [])

  const end = useCallback(async (reason = 'USER_ENDED') => {
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
      try { await dependencies.api.endRealtimeVoice(currentConversation, currentSession, reason) } catch { /* local privacy cleanup already won */ }
    }
    const currentStatus = statusRef.current
    setVoice(currentStatus?.state === 'READY' ? { state: 'IDLE' } : { state: 'UNAVAILABLE', blockedCode: currentStatus?.blockedCode ?? 'Realtime voice is unavailable.' })
  }, [closeLocal, dependencies.api, persistCapturedTurn, takeTurn])

  const fail = useCallback(async (code: string, reason = 'ERROR') => {
    const currentSession = sessionId.current
    const currentConversation = activeConversation.current
    const failedTurn = takeTurn(assistantText.current, null, 'FAILED')
    ended.current = true
    closeLocal()
    sessionId.current = null
    void persistCapturedTurn(failedTurn)
    setVoice({ state: reason === 'INTERRUPTED' ? 'INTERRUPTED' : 'ERROR', blockedCode: code })
    if (currentSession && currentConversation) {
      try { await dependencies.api.endRealtimeVoice(currentConversation, currentSession, reason) } catch { /* local privacy cleanup already won */ }
    }
  }, [closeLocal, dependencies.api, persistCapturedTurn, takeTurn])

  const saveTurn = useCallback(async (assistantTranscript: string, outputItemId: string | null, disposition: RealtimeTurnInput['assistantDisposition']) => {
    const turn = takeTurn(assistantTranscript, outputItemId, disposition)
    if (!await persistCapturedTurn(turn))
      setVoice((current) => ({ ...current, state: 'ERROR', blockedCode: 'realtime_turn_save_failed' }))
  }, [persistCapturedTurn, takeTurn])

  const runTool = useCallback(async (callId: string, name: string, args: unknown) => {
    const currentSession = sessionId.current
    const currentConversation = activeConversation.current
    if (!currentSession || !currentConversation || !args || typeof args !== 'object' || Array.isArray(args)) return
    const currentGeneration = generation.current
    const toolKey = `rt-${dependencies.createId()}`
    setVoice((current) => ({ ...current, state: 'TOOL_RUNNING', toolName: name }))
    try {
      let result = await dependencies.api.invokeRealtimeTool(currentConversation, currentSession, callId, name, args as Record<string, unknown>, toolKey)
      if (result.state === 'APPROVAL_REQUIRED' && result.actionId) {
        setVoice((current) => ({ ...current, state: 'APPROVAL_REQUIRED', toolName: name }))
        onApprovalRequiredRef.current?.(result.actionId)
        while (result.state === 'APPROVAL_REQUIRED') {
          await dependencies.wait(1000)
          if (ended.current || generation.current !== currentGeneration) return
          result = await dependencies.api.invokeRealtimeTool(currentConversation, currentSession, callId, name, args as Record<string, unknown>, toolKey)
        }
      }
      if (channel.current?.readyState === 'open') {
        const output = result.state === 'COMPLETED' && result.output ? result.output : { error: result.errorCode ?? 'tool_failed' }
        channel.current.send(JSON.stringify({ type: 'conversation.item.create', item: { type: 'function_call_output', call_id: callId, output: JSON.stringify(output) } }))
        channel.current.send(JSON.stringify({ type: 'response.create' }))
      }
      setVoice((current) => ({ ...current, state: 'LISTENING', toolName: undefined }))
    } catch { void fail('realtime_tool_failed') }
  }, [dependencies.api, fail])

  const handleData = useCallback((raw: string) => {
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
    } else if (event.type === 'provider_error') {
      void fail(event.code)
    }
  }, [fail, runTool, saveTurn])

  const start = useCallback(async () => {
    if (!conversationId || status?.state !== 'READY' || !ended.current) return
    const currentGeneration = ++generation.current
    ended.current = false
    activeConversation.current = conversationId
    setVoice({ state: 'REQUESTING_PERMISSION' })
    try {
      const localStream = await dependencies.getUserMedia()
      if (generation.current !== currentGeneration) { localStream.getTracks().forEach((track) => track.stop()); return }
      stream.current = localStream
      const connection = dependencies.createPeerConnection()
      peer.current = connection
      const remoteAudio = dependencies.createAudio()
      audio.current = remoteAudio
      connection.ontrack = (event) => {
        if (!event.streams[0]) return
        remoteAudio.srcObject = event.streams[0]
        void remoteAudio.play().catch(() => setVoice((current) => ({ ...current, state: 'AUDIO_OUTPUT_BLOCKED' })))
      }
      connection.onconnectionstatechange = () => {
        if (connection.connectionState === 'connected') setVoice((current) => ({ ...current, state: 'LISTENING' }))
        if (connection.connectionState === 'failed' || connection.connectionState === 'disconnected') {
          void fail('realtime_connection_interrupted', 'INTERRUPTED')
        }
      }
      localStream.getTracks().forEach((track) => connection.addTrack(track, localStream))
      const dataChannel = connection.createDataChannel('realtime-channel')
      channel.current = dataChannel
      dataChannel.onopen = () => setVoice((current) => ({ ...current, state: 'LISTENING' }))
      dataChannel.onmessage = (event) => typeof event.data === 'string' && handleData(event.data)
      dataChannel.onerror = () => { void fail('realtime_data_channel_failed') }
      setVoice({ state: 'NEGOTIATING' })
      const offer = await connection.createOffer({ offerToReceiveAudio: true })
      await connection.setLocalDescription(offer)
      if (!offer.sdp) throw new Error('realtime_offer_invalid')
      const negotiation = await dependencies.api.negotiateRealtimeVoice(conversationId, dependencies.createId(), offer.sdp)
      if (generation.current !== currentGeneration) return
      sessionId.current = negotiation.sessionId
      await connection.setRemoteDescription({ type: 'answer', sdp: negotiation.answerSdp })
      const expiresIn = Math.max(0, Math.min(negotiation.maxSessionSeconds * 1000, new Date(negotiation.expiresAt).valueOf() - Date.now()))
      expiryTimer.current = setTimeout(() => {
        const expiredSession = sessionId.current
        const interruptedTurn = takeTurn(assistantText.current, null, 'INTERRUPTED')
        ended.current = true
        closeLocal()
        sessionId.current = null
        void persistCapturedTurn(interruptedTurn)
        setVoice({ state: 'SESSION_EXPIRED' })
        if (expiredSession) void dependencies.api.endRealtimeVoice(conversationId, expiredSession, 'EXPIRED').catch(() => undefined)
      }, expiresIn)
    } catch (cause) {
      const code = cause instanceof DOMException && cause.name === 'NotAllowedError' ? 'PERMISSION_DENIED' : 'ERROR'
      const message = cause instanceof Error ? cause.message : 'realtime_start_failed'
      if (code === 'PERMISSION_DENIED') { closeLocal(); ended.current = true; setVoice({ state: code, blockedCode: message }) }
      else await fail(message)
    }
  }, [closeLocal, conversationId, dependencies, fail, handleData, persistCapturedTurn, status?.state, takeTurn])

  const toggleMute = useCallback(() => {
    const nextMuted = stream.current?.getAudioTracks().some((track) => track.enabled) ?? false
    stream.current?.getAudioTracks().forEach((track) => { track.enabled = !nextMuted })
    setVoice((current) => ({ ...current, muted: nextMuted }))
  }, [])

  const interrupt = useCallback(() => {
    if (channel.current?.readyState === 'open') {
      channel.current.send(JSON.stringify({ type: 'response.cancel' }))
      channel.current.send(JSON.stringify({ type: 'output_audio_buffer.clear' }))
    }
    setVoice((current) => ({ ...current, state: 'LISTENING', assistantCaption: undefined }))
  }, [])

  const enableAudio = useCallback(() => {
    void audio.current?.play().then(() => setVoice((current) => ({ ...current, state: 'LISTENING' }))).catch(() => undefined)
  }, [])

  useEffect(() => {
    if (!ended.current && activeConversation.current !== conversationId) void end('CONVERSATION_CHANGED')
    activeConversation.current = conversationId
  }, [conversationId, end])

  useEffect(() => {
    if (ended.current) setVoice(status?.state === 'READY' ? { state: 'IDLE' } : { state: 'UNAVAILABLE', blockedCode: status?.blockedCode ?? (status?.state === 'CHECKING' ? 'Checking realtime voice…' : 'Realtime voice is unavailable.') })
  }, [status])

  useEffect(() => {
    const pageHide = () => { void end('PAGE_CLOSED') }
    window.addEventListener('pagehide', pageHide)
    return () => { window.removeEventListener('pagehide', pageHide); void end('PAGE_CLOSED') }
  }, [end])

  return { voice, start, retry: start, toggleMute, interrupt, end, enableAudio }
}
