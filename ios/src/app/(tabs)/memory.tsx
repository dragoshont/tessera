import { useEffect, useState } from 'react'
import { Alert, FlatList, Pressable, RefreshControl, StyleSheet, Text, View } from 'react-native'
import type { Memory, MemoryWhy } from '@tessera/client'

import { Button, Card, Empty, ErrorState, Icon, Loading, Status, formatTime, sharedStyles } from '@/components/ui'
import { Space, usePalette } from '@/constants/theme'
import { useSession } from '@/providers/session'

export default function MemoryScreen() {
  const palette = usePalette()
  const { api } = useSession()
  const [items, setItems] = useState<Memory[]>([])
  const [selected, setSelected] = useState<string | null>(null)
  const [why, setWhy] = useState<MemoryWhy | null>(null)
  const [loading, setLoading] = useState(true)
  const [refreshing, setRefreshing] = useState(false)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const load = async (refresh = false) => {
    refresh ? setRefreshing(true) : setLoading(true)
    setError(null)
    try { setItems((await api.memory()).items) } catch (cause) { setError(cause instanceof Error ? cause.message : 'Memory unavailable') }
    finally { setLoading(false); setRefreshing(false) }
  }
  useEffect(() => { void load() }, [])

  const inspect = async (item: Memory) => {
    if (selected === item.assertionId) { setSelected(null); setWhy(null); return }
    setSelected(item.assertionId)
    setWhy(null)
    try { setWhy(await api.memoryWhy(item.assertionId)) } catch (cause) { setError(cause instanceof Error ? cause.message : 'Evidence unavailable') }
  }
  const stop = (item: Memory) => Alert.alert('Stop using this memory?', 'Tessera will retain its history and evidence, but will no longer use the assertion in future context.', [
    { text: 'Keep using', style: 'cancel' },
    { text: 'Stop using', style: 'destructive', onPress: () => void (async () => { setBusy(item.assertionId); try { await api.stopUsingMemory(item); await load(true) } catch (cause) { setError(cause instanceof Error ? cause.message : 'Update failed') } finally { setBusy(null) } })() },
  ])

  if (loading) return <View style={[sharedStyles.page, { backgroundColor: palette.background }]}><Loading /></View>
  if (error && !items.length) return <View style={[sharedStyles.page, { backgroundColor: palette.background }]}><ErrorState message={error} retry={() => void load()} /></View>
  return (
    <View style={[sharedStyles.page, { backgroundColor: palette.background }]}>
      <FlatList data={items} keyExtractor={(item) => item.assertionId} contentContainerStyle={[sharedStyles.listContent, !items.length && styles.empty]} refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void load(true)} />} ListEmptyComponent={<Empty icon="brain.head.profile" title="Nothing remembered yet" detail="Ask Tessera to remember something in Chat. You’ll review it before it becomes active memory." />} renderItem={({ item }) => (
        <Card>
          <Pressable accessibilityRole="button" accessibilityState={{ expanded: selected === item.assertionId }} onPress={() => void inspect(item)} style={[sharedStyles.split, styles.disclosure]}>
            <View style={{ flex: 1 }}><Text style={[sharedStyles.detail, { color: palette.muted }]}>{item.subjectKey} · {item.predicate}</Text><Text style={[sharedStyles.title, { color: palette.text }]}>{item.value}</Text></View>
            <Icon name={selected === item.assertionId ? 'chevron.up' : 'chevron.down'} color={palette.muted} size={16} />
          </Pressable>
          <View style={sharedStyles.split}><Status value={item.status} /><Text style={[sharedStyles.detail, { color: palette.muted }]}>Since {formatTime(item.validFrom)}</Text></View>
          {selected === item.assertionId ? <View style={[styles.why, { borderTopColor: palette.line }]}>{why ? <><Text style={[sharedStyles.title, { color: palette.text }]}>Why Tessera remembers this</Text>{why.evidence.length ? why.evidence.map((evidence) => <View key={evidence.evidenceId} style={{ gap: Space.xs }}><Text style={[sharedStyles.detail, { color: palette.muted }]}>{evidence.sourceType} · {formatTime(evidence.observedAt)}</Text><Text style={[sharedStyles.body, { color: palette.text }]}>{evidence.boundedExcerpt ?? evidence.sourceLocator}</Text></View>) : <Text style={[sharedStyles.detail, { color: palette.muted }]}>No bounded evidence excerpt is available.</Text>}<Button label="Stop using" icon="nosign" tone="danger" busy={busy === item.assertionId} onPress={() => stop(item)} /></> : <Loading />}</View> : null}
        </Card>
      )} />
      {error ? <Text style={[styles.error, { color: palette.danger }]}>{error}</Text> : null}
    </View>
  )
}
const styles = StyleSheet.create({ empty: { flexGrow: 1 }, disclosure: { minHeight: 44 }, why: { borderTopWidth: StyleSheet.hairlineWidth, paddingTop: Space.md, gap: Space.md }, error: { padding: Space.lg, fontSize: 13 } })