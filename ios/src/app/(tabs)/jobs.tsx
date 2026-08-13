import { useEffect, useRef, useState } from 'react'
import { FlatList, Pressable, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native'
import { router } from 'expo-router'
import { GenerationFence, type Conversation, type DevelopmentWorkspace, type Job, TesseraProblem } from '@tessera/client'

import { Button, Card, Empty, ErrorState, Loading, SectionTitle, Status, formatTime, sharedStyles } from '@/components/ui'
import { Radius, Space, usePalette } from '@/constants/theme'
import { useSession } from '@/providers/session'

export default function JobsScreen() {
  const palette = usePalette()
  const { api } = useSession()
  const [items, setItems] = useState<Job[]>([])
  const [conversations, setConversations] = useState<Conversation[]>([])
  const [workspaces, setWorkspaces] = useState<DevelopmentWorkspace[]>([])
  const [conversationId, setConversationId] = useState('')
  const [workspaceId, setWorkspaceId] = useState('')
  const [loading, setLoading] = useState(true)
  const [loadingWorkspaces, setLoadingWorkspaces] = useState(false)
  const [launching, setLaunching] = useState(false)
  const [refreshing, setRefreshing] = useState(false)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [launcherError, setLauncherError] = useState<string | null>(null)
  const workspaceFence = useRef(new GenerationFence())

  const loadWorkspaces = async (selectedConversationId: string) => {
    workspaceFence.current.invalidate()
    setWorkspaceId('')
    setWorkspaces([])
    setLauncherError(null)
    if (!selectedConversationId) return
    setLoadingWorkspaces(true)
    const result = await workspaceFence.current.runLatest(() => api.developmentWorkspaces(selectedConversationId))
    if (!result.current) return
    setLoadingWorkspaces(false)
    if (result.error) setLauncherError(result.error instanceof Error ? result.error.message : 'Couldn’t load workspaces.')
    else setWorkspaces(result.value!.items)
  }

  const load = async (refresh = false) => {
    refresh ? setRefreshing(true) : setLoading(true)
    setError(null)
    try {
      const [jobs, availableConversations] = await Promise.all([api.jobs(), api.conversations()])
      setItems(jobs.items)
      setConversations(availableConversations.items.filter((item) => item.state !== 'DELETED'))
      if (conversationId) await loadWorkspaces(conversationId)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Jobs unavailable') }
    finally { setLoading(false); setRefreshing(false) }
  }
  useEffect(() => { void load(); return () => workspaceFence.current.invalidate() }, [])

  const selectConversation = (id: string) => {
    setConversationId(id)
    void loadWorkspaces(id)
  }

  const launchDevelopment = async () => {
    const workspace = workspaces.find((item) => item.id === workspaceId)
    if (!conversationId || !workspace || launching) return
    setLaunching(true)
    setLauncherError(null)
    try {
      const task = await api.createDevelopmentTask(conversationId, {
        name: `Repository status: ${workspace.displayName}`,
        workspaceId: workspace.id,
        commandProfile: 'repository.status',
        arguments: [],
      })
      setItems((current) => [task.job, ...current.filter((item) => item.id !== task.job.id)])
      router.push(`/job-run/${task.run.id}?jobId=${task.job.id}` as never)
    } catch (cause) {
      const code = cause instanceof TesseraProblem ? cause.code : cause instanceof Error ? cause.message : 'development_task_failed'
      setLauncherError(code === 'development_executor_unavailable'
        ? "The server's development executor is not configured."
        : code === 'workspace_unavailable'
          ? 'The selected workspace is no longer available. Choose another ready workspace.'
          : code)
    } finally { setLaunching(false) }
  }

  const operate = async (item: Job, operation: 'run' | 'pause' | 'resume') => {
    setBusy(`${item.id}:${operation}`)
    setError(null)
    try {
      if (operation === 'run') await api.runJob(item)
      else await api.setJobState(item, operation)
      await load(true)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Job operation failed') }
    finally { setBusy(null) }
  }

  if (loading) return <View style={[sharedStyles.page, { backgroundColor: palette.background }]}><Loading /></View>
  if (error && !items.length) return <View style={[sharedStyles.page, { backgroundColor: palette.background }]}><ErrorState message={error} retry={() => void load()} /></View>
  return (
    <View style={[sharedStyles.page, { backgroundColor: palette.background }]}>
      <FlatList data={items} keyExtractor={(item) => item.id} contentContainerStyle={[sharedStyles.listContent, !items.length && styles.empty]} refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void load(true)} />} ListHeaderComponent={<View style={[styles.launcher, { borderColor: palette.line }]}>
        <SectionTitle detail="Read one immutable server snapshot. No repository changes are made.">Run repository status</SectionTitle>
        <Text style={[sharedStyles.detail, { color: palette.muted }]}>Conversation</Text>
        {conversations.length ? <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.choices}>{conversations.map((item) => <Pressable key={item.id} accessibilityRole="button" accessibilityLabel={`Conversation, ${item.title}, ${item.state}`} accessibilityHint="Double-tap to choose" accessibilityState={{ selected: conversationId === item.id }} onPress={() => selectConversation(item.id)} style={[styles.choice, { backgroundColor: conversationId === item.id ? palette.accentSoft : palette.surface, borderColor: conversationId === item.id ? palette.accent : palette.line }]}><Text numberOfLines={1} style={[styles.choiceText, { color: palette.text }]}>{item.title}</Text></Pressable>)}</ScrollView> : <Text style={[sharedStyles.body, { color: palette.muted }]}>No conversations yet. Create one in Chat first.</Text>}
        {conversationId ? <><Text style={[sharedStyles.detail, { color: palette.muted }]}>Workspace</Text>{loadingWorkspaces ? <Text accessibilityLiveRegion="polite" style={[sharedStyles.body, { color: palette.muted }]}>Loading workspaces…</Text> : workspaces.length ? <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.choices}>{workspaces.map((item) => <Pressable key={item.id} accessibilityRole="button" accessibilityLabel={`Workspace, ${item.displayName}, snapshot ${item.snapshotHash}`} accessibilityHint="Double-tap to choose" accessibilityState={{ selected: workspaceId === item.id }} onPress={() => setWorkspaceId(item.id)} style={[styles.choice, { backgroundColor: workspaceId === item.id ? palette.accentSoft : palette.surface, borderColor: workspaceId === item.id ? palette.accent : palette.line }]}><Text numberOfLines={1} style={[styles.choiceText, { color: palette.text }]}>{item.displayName}</Text><Text numberOfLines={1} style={[styles.hash, { color: palette.muted }]}>{item.snapshotHash}</Text></Pressable>)}</ScrollView> : <Text style={[sharedStyles.body, { color: palette.muted }]}>No ready workspaces for this conversation.</Text>}</> : null}
        <View style={[styles.command, { borderColor: palette.line }]}><View><Text style={[sharedStyles.body, { color: palette.text, fontWeight: '600' }]}>Repository status</Text><Text style={[styles.hash, { color: palette.muted }]}>repository.status · read only</Text></View><Status value="READ ONLY" /></View>
        {launcherError ? <Text accessibilityRole="alert" style={[styles.error, { color: palette.danger }]}>{launcherError}</Text> : null}
        <Button label="Run repository status" icon="play.fill" tone="primary" busy={launching} disabled={!conversationId || !workspaceId} onPress={() => void launchDevelopment()} />
      </View>} ListEmptyComponent={<Empty icon="calendar.badge.clock" title="No Jobs yet" detail="Jobs run on your home server, even when this phone is offline." />} renderItem={({ item }) => (
        <Card>
          <View style={sharedStyles.split}><Text style={[sharedStyles.title, { color: palette.text, flex: 1 }]}>{item.name}</Text><Status value={item.health} /></View>
          {item.kind === 'DEVELOPMENT' ? <Text style={[sharedStyles.detail, { color: palette.muted }]}>Development · {item.developmentSpec?.commandProfile}</Text> : null}
          <Text style={[sharedStyles.body, { color: palette.text }]} numberOfLines={3}>{item.instruction}</Text>
          <View><Text style={[sharedStyles.detail, { color: palette.muted }]}>Next run</Text><Text style={[sharedStyles.body, { color: palette.text }]}>{formatTime(item.nextOccurrence)}</Text></View>
          {item.lastRun ? <View><Text style={[sharedStyles.detail, { color: palette.muted }]}>Last run</Text><Text style={[sharedStyles.body, { color: palette.text }]}>{formatTime(item.lastRun.startedAt)} · {item.lastRun.state.replaceAll('_', ' ')}</Text></View> : null}
          <View style={sharedStyles.actions}>{item.kind === 'DEVELOPMENT' ? item.lastRun ? <Button label="View run" icon="terminal" onPress={() => router.push(`/job-run/${item.lastRun!.id}?jobId=${item.id}` as never)} /> : null : <><Button label="Run now" icon="play.fill" busy={busy === `${item.id}:run`} disabled={Boolean(busy)} onPress={() => void operate(item, 'run')} /><Button label={item.desiredState === 'PAUSED' ? 'Resume' : 'Pause'} icon={item.desiredState === 'PAUSED' ? 'play' : 'pause'} busy={busy === `${item.id}:${item.desiredState === 'PAUSED' ? 'resume' : 'pause'}`} disabled={Boolean(busy)} onPress={() => void operate(item, item.desiredState === 'PAUSED' ? 'resume' : 'pause')} /></>}</View>
        </Card>
      )} />
      {error ? <Text style={[styles.error, { color: palette.danger }]}>{error}</Text> : null}
    </View>
  )
}
const styles = StyleSheet.create({
  empty: { flexGrow: 1 },
  launcher: { borderBottomWidth: StyleSheet.hairlineWidth, paddingBottom: Space.xl, gap: Space.md },
  choices: { gap: Space.sm },
  choice: { minHeight: 44, minWidth: 130, maxWidth: 250, borderWidth: StyleSheet.hairlineWidth, borderRadius: Radius.sm, paddingHorizontal: Space.md, paddingVertical: Space.sm, justifyContent: 'center' },
  choiceText: { fontSize: 15, fontWeight: '600' },
  hash: { fontSize: 12, fontFamily: 'Menlo' },
  command: { minHeight: 52, borderTopWidth: StyleSheet.hairlineWidth, borderBottomWidth: StyleSheet.hairlineWidth, flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: Space.md, paddingVertical: Space.sm },
  error: { fontSize: 13, lineHeight: 18 },
})