import { useEffect, useState } from 'react'
import { Alert, ScrollView, StyleSheet, Switch, Text, View } from 'react-native'
import * as Notifications from 'expo-notifications'

import { Button, Card, SectionTitle, Status, formatTime, sharedStyles } from '@/components/ui'
import { Space, usePalette } from '@/constants/theme'
import { useSession } from '@/providers/session'

function DiagnosticRow({ label, value }: { label: string; value: string }) {
  const palette = usePalette()
  return <View style={sharedStyles.split}><Text style={[sharedStyles.detail, { color: palette.muted }]}>{label}</Text><Text selectable style={[sharedStyles.body, styles.value, { color: palette.text }]}>{value}</Text></View>
}

export default function SettingsScreen() {
  const palette = usePalette()
  const session = useSession()
  const [notificationStatus, setNotificationStatus] = useState('unknown')
  const [busy, setBusy] = useState(false)
  useEffect(() => { void Notifications.getPermissionsAsync().then((value) => setNotificationStatus(value.status)) }, [])
  const requestNotifications = async () => {
    setBusy(true)
    try {
      const value = await Notifications.requestPermissionsAsync({ ios: { allowAlert: true, allowBadge: true, allowSound: false } })
      setNotificationStatus(value.status)
      if (value.granted) await Notifications.scheduleNotificationAsync({ content: { title: 'Tessera test notification', body: 'Local notifications work on this device.', data: { url: '/settings' } }, trigger: null })
    } finally { setBusy(false) }
  }
  const confirmSignOut = () => Alert.alert('Sign out of Tessera?', 'This removes the session from Keychain. Server-side Jobs and canonical data are not affected.', [{ text: 'Stay signed in', style: 'cancel' }, { text: 'Sign out', style: 'destructive', onPress: () => void session.signOut() }])
  return (
    <ScrollView style={{ backgroundColor: palette.background }} contentContainerStyle={sharedStyles.listContent}>
      <SectionTitle detail="No secrets are shown">Connection</SectionTitle>
      <Card>
        <View style={sharedStyles.split}><Text style={[sharedStyles.title, { color: palette.text }]}>{session.descriptor?.displayName ?? 'Tessera Home'}</Text><Status value={session.diagnostics.state} /></View>
        <DiagnosticRow label="Route" value={session.diagnostics.route ? session.diagnostics.route[0] + session.diagnostics.route.slice(1).toLowerCase() : 'None'} />
        <DiagnosticRow label="Latency" value={session.diagnostics.latencyMs === null ? 'Unavailable' : `${session.diagnostics.latencyMs} ms`} />
        <DiagnosticRow label="Server ID" value={session.diagnostics.serverId ?? 'Unverified'} />
        <DiagnosticRow label="Server version" value={session.diagnostics.serverVersion ?? 'Unavailable'} />
        <DiagnosticRow label="Client version" value={session.diagnostics.clientVersion} />
        <DiagnosticRow label="Last successful connection" value={formatTime(session.diagnostics.lastSuccessfulConnection)} />
        <Button label="Test connection" icon="arrow.triangle.2.circlepath" busy={busy} onPress={() => void session.reconnect()} />
      </Card>
      <SectionTitle>Security</SectionTitle>
      <Card>
        <View style={sharedStyles.split}><View style={{ flex: 1 }}><Text style={[sharedStyles.title, { color: palette.text }]}>App lock</Text><Text style={[sharedStyles.detail, { color: palette.muted }]}>Require Face ID, Touch ID, or device passcode after Tessera leaves the foreground.</Text></View><Switch value={session.lockEnabled} onValueChange={(value) => void session.setLockEnabled(value)} trackColor={{ true: palette.accent }} /></View>
        <DiagnosticRow label="Signed in as" value={session.principal?.principal ?? 'Unknown'} />
        <DiagnosticRow label="Role" value={session.principal?.role ?? 'Unknown'} />
      </Card>
      <SectionTitle>Notifications</SectionTitle>
      <Card><View style={sharedStyles.split}><View style={{ flex: 1 }}><Text style={[sharedStyles.title, { color: palette.text }]}>Local notification test</Text><Text style={[sharedStyles.detail, { color: palette.muted }]}>Permission: {notificationStatus}</Text></View><Status value={notificationStatus} /></View><Button label="Send test notification" icon="bell.badge" busy={busy} onPress={() => void requestNotifications()} /></Card>
      <SectionTitle>Session</SectionTitle>
      <Button label="Sign out" icon="rectangle.portrait.and.arrow.right" tone="danger" onPress={confirmSignOut} />
    </ScrollView>
  )
}
const styles = StyleSheet.create({ value: { flex: 1, textAlign: 'right' } })