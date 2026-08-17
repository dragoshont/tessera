import { useEffect, useState } from 'react'
import { FlatList, Pressable, RefreshControl, StyleSheet, Text, View } from 'react-native'
import { router } from 'expo-router'
import type { Action } from '@tessera/client'

import { Button, Card, Empty, Icon, Loading, SectionTitle, Status, formatTime, sharedStyles } from '@/components/ui'
import { Radius, Space, usePalette } from '@/constants/theme'
import { useSession } from '@/providers/session'

const destinations = [
  { title: 'Remote', detail: 'Supervise paired Macs and durable work', icon: 'laptopcomputer.and.iphone' as const, href: '/remote' as const },
  { title: 'Plugins', detail: 'Capabilities and runtime status', icon: 'shippingbox.fill' as const, href: '/plugins' as const },
  { title: 'Activity', detail: 'Auditable product history', icon: 'waveform.path.ecg' as const, href: '/activity' as const },
  { title: 'Settings', detail: 'Connection, security, notifications', icon: 'gearshape.fill' as const, href: '/settings' as const },
]

export default function MoreScreen() {
  const palette = usePalette()
  const { api } = useSession()
  const [actions, setActions] = useState<Action[]>([])
  const [loading, setLoading] = useState(true)
  const [refreshing, setRefreshing] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const load = async (refresh = false) => {
    refresh ? setRefreshing(true) : setLoading(true)
    setError(null)
    try { setActions((await api.actions()).items) }
    catch (cause) { setError(cause instanceof Error ? cause.message : 'Actions unavailable') }
    finally { setLoading(false); setRefreshing(false) }
  }
  useEffect(() => { void load() }, [])
  const pending = actions.filter((item) => item.state === 'PROPOSED' || item.state === 'RECONCILIATION_REQUIRED')

  if (loading) return <View style={[sharedStyles.page, { backgroundColor: palette.background }]}><Loading /></View>
  return (
    <FlatList style={{ backgroundColor: palette.background }} data={pending} keyExtractor={(item) => item.id} contentContainerStyle={sharedStyles.listContent} refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void load(true)} />} ListHeaderComponent={<>
      <SectionTitle detail="Open review and recovery work">Actions</SectionTitle>
      {error ? <Card><Text accessibilityRole="alert" style={[sharedStyles.body, { color: palette.danger }]}>Actions could not be loaded. Product navigation remains available.</Text><Button label="Retry actions" icon="arrow.clockwise" onPress={() => void load()} /></Card> : null}
      {!pending.length && !error ? <Card><Empty icon="checkmark.shield" title="Nothing needs review" detail="Consequential operations appear here before Tessera can execute them." /></Card> : null}
    </>} renderItem={({ item }) => (
      <Pressable accessibilityRole="button" onPress={() => router.push(`/action/${item.id}`)}>
        <Card><View style={sharedStyles.split}><Text style={[sharedStyles.title, { color: palette.text, flex: 1 }]}>{item.capabilityId}</Text><Status value={item.state} /></View><Text style={[sharedStyles.body, { color: palette.text }]} numberOfLines={2}>{item.target}</Text><Text style={[sharedStyles.detail, { color: palette.muted }]}>Expires {formatTime(item.expiresAt)}</Text></Card>
      </Pressable>
    )} ListFooterComponent={<View style={styles.footer}><SectionTitle>Product</SectionTitle>{destinations.map((item) => <Pressable key={item.href} accessibilityRole="button" onPress={() => router.push(item.href as never)} style={({ pressed }) => [styles.destination, { backgroundColor: palette.surface, borderColor: palette.line, opacity: pressed ? 0.7 : 1 }]}><Icon name={item.icon} color={palette.accent} /><View style={{ flex: 1 }}><Text style={[sharedStyles.title, { color: palette.text }]}>{item.title}</Text><Text style={[sharedStyles.detail, { color: palette.muted }]}>{item.detail}</Text></View><Icon name="chevron.right" color={palette.muted} size={15} /></Pressable>)}</View>} />
  )
}
const styles = StyleSheet.create({ footer: { gap: Space.sm }, destination: { minHeight: 66, borderRadius: Radius.md, borderWidth: StyleSheet.hairlineWidth, padding: Space.md, flexDirection: 'row', alignItems: 'center', gap: Space.md } })