import { useEffect, useState } from 'react'
import { FlatList, RefreshControl, StyleSheet, Text, View } from 'react-native'
import type { Job } from '@tessera/client'

import { Button, Card, Empty, ErrorState, Loading, Status, formatTime, sharedStyles } from '@/components/ui'
import { Space, usePalette } from '@/constants/theme'
import { useSession } from '@/providers/session'

export default function JobsScreen() {
  const palette = usePalette()
  const { api } = useSession()
  const [items, setItems] = useState<Job[]>([])
  const [loading, setLoading] = useState(true)
  const [refreshing, setRefreshing] = useState(false)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = async (refresh = false) => {
    refresh ? setRefreshing(true) : setLoading(true)
    setError(null)
    try { setItems((await api.jobs()).items) } catch (cause) { setError(cause instanceof Error ? cause.message : 'Jobs unavailable') }
    finally { setLoading(false); setRefreshing(false) }
  }
  useEffect(() => { void load() }, [])

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
      <FlatList data={items} keyExtractor={(item) => item.id} contentContainerStyle={[sharedStyles.listContent, !items.length && styles.empty]} refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void load(true)} />} ListEmptyComponent={<Empty icon="calendar.badge.clock" title="No Jobs yet" detail="Jobs run on your home server, even when this phone is offline." />} renderItem={({ item }) => (
        <Card>
          <View style={sharedStyles.split}><Text style={[sharedStyles.title, { color: palette.text, flex: 1 }]}>{item.name}</Text><Status value={item.health} /></View>
          <Text style={[sharedStyles.body, { color: palette.text }]} numberOfLines={3}>{item.instruction}</Text>
          <View><Text style={[sharedStyles.detail, { color: palette.muted }]}>Next run</Text><Text style={[sharedStyles.body, { color: palette.text }]}>{formatTime(item.nextOccurrence)}</Text></View>
          {item.lastRun ? <View><Text style={[sharedStyles.detail, { color: palette.muted }]}>Last run</Text><Text style={[sharedStyles.body, { color: palette.text }]}>{formatTime(item.lastRun.startedAt)} · {item.lastRun.state.replaceAll('_', ' ')}</Text></View> : null}
          <View style={sharedStyles.actions}><Button label="Run now" icon="play.fill" busy={busy === `${item.id}:run`} disabled={Boolean(busy)} onPress={() => void operate(item, 'run')} /><Button label={item.desiredState === 'PAUSED' ? 'Resume' : 'Pause'} icon={item.desiredState === 'PAUSED' ? 'play' : 'pause'} busy={busy === `${item.id}:${item.desiredState === 'PAUSED' ? 'resume' : 'pause'}`} disabled={Boolean(busy)} onPress={() => void operate(item, item.desiredState === 'PAUSED' ? 'resume' : 'pause')} /></View>
        </Card>
      )} />
      {error ? <Text style={[styles.error, { color: palette.danger }]}>{error}</Text> : null}
    </View>
  )
}
const styles = StyleSheet.create({ empty: { flexGrow: 1 }, error: { padding: Space.lg, fontSize: 13 } })