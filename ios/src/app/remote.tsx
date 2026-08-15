import { useEffect, useState } from 'react'
import { FlatList, Pressable, RefreshControl, StyleSheet, Text, View } from 'react-native'
import { router } from 'expo-router'
import { TesseraProblem, type RemoteHostSummaryDto } from '@tessera/client'

import { Card, Empty, ErrorState, Loading, SectionTitle, Status, formatTime, sharedStyles } from '@/components/ui'
import { Space, usePalette } from '@/constants/theme'
import { useSession } from '@/providers/session'

export function remoteHostAccessibilityLabel(host: RemoteHostSummaryDto): string {
  const lastSeen = host.lastSeenAt ? `last seen ${formatTime(host.lastSeenAt)}` : 'not seen yet'
  return `${host.displayName}, ${host.lifecycle.replaceAll('_', ' ').toLowerCase()}, ${lastSeen}, ${host.connectionStatus.replaceAll('_', ' ').toLowerCase()}`
}

export default function RemoteScreen() {
  const palette = usePalette()
  const { api } = useSession()
  const [hosts, setHosts] = useState<RemoteHostSummaryDto[]>([])
  const [loading, setLoading] = useState(true)
  const [refreshing, setRefreshing] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [unsupported, setUnsupported] = useState(false)

  const load = async (refresh = false) => {
    refresh ? setRefreshing(true) : setLoading(true)
    setError(null)
    try {
      setHosts((await api.remoteHosts()).items)
      setUnsupported(false)
    } catch (cause) {
      setUnsupported(cause instanceof TesseraProblem && [404, 405, 501].includes(cause.status))
      setError(cause instanceof Error ? cause.message : 'Remote Hosts unavailable')
    } finally { setLoading(false); setRefreshing(false) }
  }
  useEffect(() => { void load() }, [])

  if (loading) return <View style={[sharedStyles.page, { backgroundColor: palette.background }]}><Loading /></View>
  if (error && !hosts.length && !unsupported) return <View style={[sharedStyles.page, { backgroundColor: palette.background }]}><ErrorState message={error} retry={() => void load()} /></View>
  return (
    <FlatList
      style={{ backgroundColor: palette.background }}
      data={hosts}
      keyExtractor={(item) => item.hostId}
      contentContainerStyle={[sharedStyles.listContent, !hosts.length && styles.empty]}
      refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void load(true)} />}
      ListHeaderComponent={<View style={styles.header}>
        <SectionTitle detail="Pair trusted Macs and supervise canonical Jobs. Server Jobs continue normally with no Hosts configured.">Remote Host preview</SectionTitle>
        <Card><Text style={[sharedStyles.title, { color: palette.text }]}>{unsupported ? 'Remote Hosts unavailable' : 'Pairing not available in this preview'}</Text><Text style={[sharedStyles.detail, { color: palette.muted }]}>{unsupported ? 'This Tessera server does not expose the Remote Host API.' : 'Pairing waits for the signed Mac helper journey. This phone never receives a Host key or repository path.'}</Text></Card>
      </View>}
      ListEmptyComponent={<Empty icon="laptopcomputer.slash" title="No Macs are paired" detail="Server Jobs continue normally. Pull to refresh after pairing from a verified client." />}
      renderItem={({ item }) => (
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={remoteHostAccessibilityLabel(item)}
          accessibilityHint="Opens Remote Host details"
          onPress={() => router.push(`/remote-host/${encodeURIComponent(item.hostId)}` as never)}
        >
          <Card>
            <View style={sharedStyles.split}><View style={{ flex: 1 }}><Text style={[sharedStyles.title, { color: palette.text }]}>{item.displayName}</Text><Text style={[sharedStyles.detail, { color: palette.muted }]}>{item.platform} · {item.architecture}</Text></View><Status value={item.lifecycle} /></View>
            <Text style={[sharedStyles.detail, { color: palette.muted }]}>Agent {item.agentVersion} · protocol {item.protocolVersion}</Text>
            <Text style={[sharedStyles.detail, { color: palette.muted }]}>Last seen {formatTime(item.lastSeenAt)}</Text>
          </Card>
        </Pressable>
      )}
    />
  )
}

const styles = StyleSheet.create({ header: { gap: Space.md }, empty: { flexGrow: 1 } })