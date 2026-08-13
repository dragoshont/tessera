import { useEffect, useRef, useState } from 'react'
import { FlatList, KeyboardAvoidingView, Platform, Pressable, RefreshControl, ScrollView, StyleSheet, Text, TextInput, View } from 'react-native'
import { router } from 'expo-router'
import { GenerationFence, type Conversation, type Message, type ModelProfile, type RealtimeVoiceStatus } from '@tessera/client'

import { Button, Empty, ErrorState, Icon, Loading, Status, sharedStyles } from '@/components/ui'
import { Radius, Space, usePalette } from '@/constants/theme'
import { useSession } from '@/providers/session'
import { useNativeRealtimeVoice, type NativeVoiceView } from '@/hooks/use-native-realtime-voice'

export default function ChatScreen() {
  const palette = usePalette()
  const { api } = useSession()
  const [conversations, setConversations] = useState<Conversation[]>([])
  const [conversation, setConversation] = useState<Conversation | null>(null)
  const [profile, setProfile] = useState<ModelProfile | null>(null)
  const [messages, setMessages] = useState<Message[]>([])
  const [voiceStatus, setVoiceStatus] = useState<RealtimeVoiceStatus>()
  const [input, setInput] = useState('')
  const [streamStatus, setStreamStatus] = useState<string | null>(null)
  const [streamingText, setStreamingText] = useState('')
  const [loading, setLoading] = useState(true)
  const [refreshing, setRefreshing] = useState(false)
  const [sending, setSending] = useState(false)
  const [creating, setCreating] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const list = useRef<FlatList<Message>>(null)
  const activeConversationId = useRef<string | null>(null)
  const messageFence = useRef(new GenerationFence())
  const streamFence = useRef(new GenerationFence())
  const streamAbort = useRef<AbortController | null>(null)
  const realtimeVoice = useNativeRealtimeVoice({
    api,
    conversationId: conversation?.id,
    status: voiceStatus,
    onTurnSaved: () => conversation && void loadMessages(conversation.id),
    onApprovalRequired: (actionId) => router.push(`/action/${actionId}`),
  })

  const loadMessages = async (id: string) => {
    const result = await messageFence.current.runLatest(() => api.messages(id))
    if (!result.current || activeConversationId.current !== id) return
    if (result.error) throw result.error
    setMessages(result.value!.items)
  }
  const load = async (refresh = false) => {
    refresh ? setRefreshing(true) : setLoading(true)
    setError(null)
    try {
      const setup = await api.setupStatus()
      if (setup.ai.state === 'READY_TO_CONNECT') await api.bootstrapSetup()
      const [conversations, profiles, settings, realtimeStatus] = await Promise.all([
        api.conversations(), api.modelProfiles(), api.settings(),
        api.realtimeVoiceStatus().catch(() => ({ state: 'UNAVAILABLE' as const, blockedCode: 'Realtime voice is not enabled on this server.', supportsTools: false, maxSessionSeconds: 900, checkedAt: null, validUntil: null, version: 1 })),
      ])
      const selectedProfile = profiles.items.find((item) => item.profileId === settings.defaultChatModelProfileId) ?? profiles.items.find((item) => item.enabled) ?? null
      const selectedConversation = conversations.items.find((item) => item.id === activeConversationId.current)
        ?? conversations.items.find((item) => item.state === 'ACTIVE')
        ?? null
      setConversations(conversations.items.filter((item) => item.state !== 'DELETED'))
      setProfile(selectedProfile)
      setVoiceStatus(realtimeStatus)
      setConversation(selectedConversation)
      activeConversationId.current = selectedConversation?.id ?? null
      if (selectedConversation) await loadMessages(selectedConversation.id)
      else setMessages([])
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Chat is unavailable') }
    finally { setLoading(false); setRefreshing(false) }
  }

  useEffect(() => { void load() }, [])
  useEffect(() => () => streamAbort.current?.abort(), [])
  useEffect(() => { if (messages.length) requestAnimationFrame(() => list.current?.scrollToEnd({ animated: true })) }, [messages])

  const send = async () => {
    const text = input.trim()
    if (!text || !profile || sending) return
    setSending(true)
    setError(null)
    streamAbort.current?.abort()
    streamFence.current.invalidate()
    const streamGeneration = streamFence.current.capture()
    try {
      const active = conversation ?? await api.createConversation(profile.profileId)
      if (!streamFence.current.isCurrent(streamGeneration)) return
      if (!conversation) {
        setConversations((current) => [active, ...current])
        setConversation(active)
        activeConversationId.current = active.id
      }
      const result = await api.sendMessage(active.id, profile.profileId, text)
      if (!streamFence.current.isCurrent(streamGeneration) || activeConversationId.current !== active.id) return
      setInput('')
      await loadMessages(active.id)
      setStreamStatus('Tessera is working')
      setStreamingText('')
      const controller = new AbortController()
      streamAbort.current = controller
      try {
        await api.watchExecution(active.id, result.executionId, controller.signal, (event) => {
          if (!streamFence.current.isCurrent(streamGeneration) || activeConversationId.current !== active.id) return
          setStreamStatus(event.type.replaceAll('_', ' '))
          if (event.type === 'text' && typeof event.data === 'object' && event.data !== null && 'delta' in event.data && typeof event.data.delta === 'string') {
            const delta = event.data.delta
            setStreamingText((current) => `${current}${delta}`.slice(0, 100_000))
          }
        })
      } finally {
        if (streamFence.current.isCurrent(streamGeneration) && activeConversationId.current === active.id) {
          setStreamStatus(null)
          await loadMessages(active.id)
          setStreamingText('')
        }
      }
    } catch (cause) {
      if (streamFence.current.isCurrent(streamGeneration)) setError(cause instanceof Error ? cause.message : 'Message failed')
    }
    finally { setSending(false) }
  }

  const selectConversation = async (item: Conversation) => {
    streamAbort.current?.abort()
    streamFence.current.invalidate()
    activeConversationId.current = item.id
    setConversation(item)
    setMessages([])
    setStreamingText('')
    setStreamStatus(null)
    setError(null)
    try { await loadMessages(item.id) } catch (cause) { setError(cause instanceof Error ? cause.message : 'Conversation unavailable') }
  }

  const createConversation = async () => {
    if (!profile || creating) return
    setCreating(true)
    setError(null)
    streamAbort.current?.abort()
    streamFence.current.invalidate()
    const generation = streamFence.current.capture()
    try {
      const item = await api.createConversation(profile.profileId)
      if (!streamFence.current.isCurrent(generation)) return
      setConversations((current) => [item, ...current])
      setConversation(item)
      activeConversationId.current = item.id
      setMessages([])
    } catch (cause) {
      if (streamFence.current.isCurrent(generation)) setError(cause instanceof Error ? cause.message : 'Conversation creation failed')
    }
    finally { setCreating(false) }
  }

  const renderMessage = ({ item }: { item: Message }) => {
    const mine = item.role === 'USER'
    const text = item.parts.filter((part) => part.text).map((part) => part.text).join('\n')
    const actionIds = item.parts.flatMap((part) => part.actionId ? [part.actionId] : [])
    return (
      <View style={[styles.message, mine ? styles.mine : styles.theirs, { backgroundColor: mine ? palette.accent : palette.surface, borderColor: palette.line }]}>
        <Text style={[styles.speaker, { color: mine ? palette.accentForegroundMuted : palette.muted }]}>{mine ? 'You' : item.role === 'ASSISTANT' ? 'Tessera' : 'System'}</Text>
        <Text style={[sharedStyles.body, { color: mine ? palette.accentForeground : palette.text }]}>{text || item.status.replaceAll('_', ' ')}</Text>
        {actionIds.map((id) => <Pressable key={id} accessibilityRole="button" accessibilityLabel="Review action" onPress={() => router.push(`/action/${id}`)} style={[styles.actionLink, { borderColor: mine ? palette.accentForegroundMuted : palette.accent }]}><Icon name="checkmark.shield" color={mine ? palette.accentForeground : palette.accent} size={16} /><Text style={{ color: mine ? palette.accentForeground : palette.accent, fontWeight: '600' }}>Review action</Text></Pressable>)}
      </View>
    )
  }

  if (loading) return <View style={[sharedStyles.page, { backgroundColor: palette.background }]}><Loading /></View>
  if (error && !messages.length) return <View style={[sharedStyles.page, { backgroundColor: palette.background }]}><ErrorState message={error} retry={() => void load()} /></View>

  return (
    <KeyboardAvoidingView style={[sharedStyles.page, { backgroundColor: palette.background }]} behavior={Platform.OS === 'ios' ? 'padding' : undefined} keyboardVerticalOffset={92}>
      {!profile ? <Empty icon="cpu" title="AI connection required" detail="Tessera could not validate the configured home AI gateway. Retry after checking server connection details." action={<Button label="Retry setup" icon="arrow.clockwise" onPress={() => void load()} />} /> : (
        <>
          <View style={[styles.conversationBar, { backgroundColor: palette.background, borderColor: palette.line }]}>
            <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.conversationList}>
              {conversations.map((item) => <Pressable key={item.id} accessibilityRole="button" onPress={() => void selectConversation(item)} style={[styles.conversationChip, { backgroundColor: conversation?.id === item.id ? palette.accentSoft : palette.surface, borderColor: conversation?.id === item.id ? palette.accent : palette.line }]}><Text numberOfLines={1} style={[styles.conversationText, { color: palette.text }]}>{item.title}</Text></Pressable>)}
            </ScrollView>
            <Button label="New" icon="square.and.pencil" busy={creating} disabled={sending} onPress={() => void createConversation()} />
          </View>
          {conversation ? <NativeVoicePanel voice={realtimeVoice.voice} onStart={() => void realtimeVoice.start()} onRetry={() => void realtimeVoice.retry()} onToggleMute={realtimeVoice.toggleMute} onInterrupt={realtimeVoice.interrupt} onEnd={() => void realtimeVoice.end()} /> : null}
          <FlatList ref={list} data={messages} keyExtractor={(item) => item.id} renderItem={renderMessage} contentContainerStyle={[styles.list, !messages.length && !streamingText && styles.emptyList]} refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void load(true)} />} ListEmptyComponent={streamingText ? null : <Empty icon="message" title="What should Tessera help with?" detail="Ask a question, inspect connected information, or explicitly ask Tessera to remember something." />} ListFooterComponent={streamingText ? <View style={[styles.message, styles.theirs, { backgroundColor: palette.surface, borderColor: palette.line }]}><Text style={[styles.speaker, { color: palette.muted }]}>Tessera · streaming</Text><Text style={[sharedStyles.body, { color: palette.text }]}>{streamingText}</Text></View> : null} />
          {streamStatus ? <View style={[styles.stream, { backgroundColor: palette.accentSoft }]}><Status value={streamStatus} /></View> : null}
          {error ? <Text accessibilityRole="alert" style={[styles.error, { color: palette.danger }]}>{error}</Text> : null}
          <View style={[styles.composer, { backgroundColor: palette.surface, borderColor: palette.line }]}>
            <TextInput value={input} onChangeText={setInput} placeholder="Message Tessera" placeholderTextColor={palette.muted} multiline maxLength={12_000} style={[styles.input, { color: palette.text }]} editable={!sending} />
            <Button label="Send" icon="arrow.up" tone="primary" busy={sending} disabled={!input.trim()} onPress={() => void send()} />
          </View>
        </>
      )}
    </KeyboardAvoidingView>
  )
}

function NativeVoicePanel({ voice, onStart, onRetry, onToggleMute, onInterrupt, onEnd }: {
  voice: NativeVoiceView; onStart: () => void; onRetry: () => void; onToggleMute: () => void; onInterrupt: () => void; onEnd: () => void
}) {
  const palette = usePalette()
  const active = ['NEGOTIATING', 'LISTENING', 'USER_SPEAKING', 'ASSISTANT_SPEAKING', 'TOOL_RUNNING', 'APPROVAL_REQUIRED', 'ENDING'].includes(voice.state)
  const retryable = ['PERMISSION_DENIED', 'INTERRUPTED', 'SESSION_EXPIRED', 'ERROR'].includes(voice.state)
  const detail = voice.state === 'UNAVAILABLE' ? (voice.blockedCode ?? 'Realtime voice is unavailable.')
    : voice.state === 'PERMISSION_DENIED' ? 'Allow microphone access in Settings, then retry explicitly.'
    : voice.state === 'NEGOTIATING' ? 'Exchanging SDP only. Audio goes directly between this iPhone and Foundry.'
    : voice.state === 'INTERRUPTED' ? 'Voice stopped after an audio or network interruption.'
    : voice.state === 'SESSION_EXPIRED' ? 'The session expired. Start a new voice session to continue.'
    : voice.state === 'TOOL_RUNNING' ? `Running ${voice.toolName ?? 'a reviewed tool'} through Tessera.`
    : voice.state === 'APPROVAL_REQUIRED' ? 'Voice cannot approve this action. Review the exact Action now.'
    : voice.state === 'ERROR' ? (voice.blockedCode ?? 'Voice ended safely. Typed Chat remains available.')
    : voice.muted ? 'Microphone muted.' : 'Captions are visible and completed turns are saved here.'
  return <View style={[styles.voice, { borderColor: palette.line, backgroundColor: palette.background }]} accessibilityLabel="Realtime voice">
    <View style={sharedStyles.split}><View style={{ flex: 1 }}><Text style={[sharedStyles.title, { color: palette.text }]}>Realtime voice</Text><Text style={[sharedStyles.detail, { color: palette.muted }]}>Direct WebRTC media</Text></View><Status value={voice.state.replaceAll('_', ' ')} /></View>
    <Text accessibilityLiveRegion="polite" style={[sharedStyles.detail, { color: palette.muted }]}>{detail}</Text>
    {voice.userCaption ? <Text style={[sharedStyles.body, { color: palette.text }]}><Text style={{ fontWeight: '700' }}>You: </Text>{voice.userCaption}</Text> : null}
    {voice.assistantCaption ? <Text style={[sharedStyles.body, { color: palette.text }]}><Text style={{ fontWeight: '700' }}>Tessera: </Text>{voice.assistantCaption}</Text> : null}
    <View style={sharedStyles.actions}>
      {voice.state === 'IDLE' ? <Button label="Start voice" icon="mic.fill" tone="primary" onPress={onStart} /> : null}
      {voice.state === 'UNAVAILABLE' ? <Button label="Voice unavailable" icon="mic.slash" disabled /> : null}
      {voice.state === 'REQUESTING_PERMISSION' || voice.state === 'NEGOTIATING' || voice.state === 'ENDING' ? <Button label={voice.state === 'ENDING' ? 'Ending voice' : 'Connecting voice'} busy disabled /> : null}
      {retryable ? <Button label="Retry voice" icon="arrow.clockwise" onPress={onRetry} /> : null}
      {active && voice.state !== 'NEGOTIATING' && voice.state !== 'ENDING' ? <Button label={voice.muted ? 'Unmute' : 'Mute'} icon={voice.muted ? 'mic.fill' : 'mic.slash'} accessibilityState={{ selected: Boolean(voice.muted) }} onPress={onToggleMute} /> : null}
      {voice.state === 'ASSISTANT_SPEAKING' ? <Button label="Interrupt" icon="stop.circle" onPress={onInterrupt} /> : null}
      {active && voice.state !== 'ENDING' ? <Button label="End voice" icon="phone.down.fill" tone="danger" onPress={onEnd} /> : null}
    </View>
  </View>
}

const styles = StyleSheet.create({
  list: { padding: Space.lg, gap: Space.md, paddingBottom: Space.xl },
  conversationBar: { borderBottomWidth: StyleSheet.hairlineWidth, padding: Space.sm, flexDirection: 'row', alignItems: 'center', gap: Space.sm },
  conversationList: { gap: Space.sm, alignItems: 'center' },
  conversationChip: { maxWidth: 180, minHeight: 44, justifyContent: 'center', borderWidth: StyleSheet.hairlineWidth, borderRadius: 22, paddingHorizontal: Space.md },
  conversationText: { fontSize: 13, fontWeight: '600' },
  voice: { borderBottomWidth: StyleSheet.hairlineWidth, padding: Space.md, gap: Space.sm },
  emptyList: { flexGrow: 1 },
  message: { maxWidth: '88%', borderRadius: Radius.md, padding: Space.md, gap: Space.xs, borderWidth: StyleSheet.hairlineWidth },
  mine: { alignSelf: 'flex-end', borderBottomRightRadius: 3 },
  theirs: { alignSelf: 'flex-start', borderBottomLeftRadius: 3 },
  speaker: { fontSize: 11, fontWeight: '700', textTransform: 'uppercase' },
  actionLink: { minHeight: 44, borderWidth: StyleSheet.hairlineWidth, borderRadius: Radius.sm, paddingHorizontal: Space.md, flexDirection: 'row', alignItems: 'center', gap: Space.sm, marginTop: Space.sm },
  stream: { paddingHorizontal: Space.lg, paddingVertical: Space.sm },
  error: { paddingHorizontal: Space.lg, paddingVertical: Space.sm, fontSize: 13 },
  composer: { borderTopWidth: StyleSheet.hairlineWidth, padding: Space.md, paddingBottom: Space.lg, gap: Space.sm },
  input: { minHeight: 44, maxHeight: 130, fontSize: 16, lineHeight: 21, paddingHorizontal: Space.sm, paddingVertical: Space.sm },
})