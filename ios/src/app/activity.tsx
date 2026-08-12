import { useEffect, useState } from 'react'
import { FlatList, RefreshControl, Text, View } from 'react-native'
import type { Activity } from '@tessera/client'

import { Card, Empty, ErrorState, Loading, Status, formatTime, sharedStyles } from '@/components/ui'
import { usePalette } from '@/constants/theme'
import { useSession } from '@/providers/session'

export default function ActivityScreen() {
  const palette = usePalette()
  const { api } = useSession()
  const [items, setItems] = useState<Activity[]>([])
  const [loading, setLoading] = useState(true)
  const [refreshing, setRefreshing] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const load = async (refresh = false) => { refresh ? setRefreshing(true) : setLoading(true); setError(null); try { setItems((await api.activity()).items) } catch (cause) { setError(cause instanceof Error ? cause.message : 'Activity unavailable') } finally { setLoading(false); setRefreshing(false) } }
  useEffect(() => { void load() }, [])
  if (loading) return <View style={[sharedStyles.page, { backgroundColor: palette.background }]}><Loading /></View>
  if (error && !items.length) return <View style={[sharedStyles.page, { backgroundColor: palette.background }]}><ErrorState message={error} retry={() => void load()} /></View>
  return <FlatList style={{ backgroundColor: palette.background }} data={items} keyExtractor={(item) => item.id} contentContainerStyle={sharedStyles.listContent} refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void load(true)} />} ListEmptyComponent={<Empty icon="waveform.path.ecg" title="No activity yet" detail="Chat, Jobs, Actions, Account, and Plugin events appear here with evidence references." />} renderItem={({ item }) => <Card><View style={sharedStyles.split}><Text style={[sharedStyles.detail, { color: palette.muted }]}>{formatTime(item.occurredAt)}</Text>{item.state ? <Status value={item.state} /> : null}</View><Text style={[sharedStyles.title, { color: palette.text }]}>{item.summary}</Text><Text style={[sharedStyles.detail, { color: palette.muted }]}>{item.kind.replaceAll('_', ' ')} · {item.resourceType}</Text></Card>} />
}