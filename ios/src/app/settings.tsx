import { useEffect, useState } from 'react'
import { Alert, AppState, Linking, ScrollView, StyleSheet, Switch, Text, View } from 'react-native'
import * as Notifications from 'expo-notifications'

import { Button, Card, SectionTitle, Status, formatTime, sharedStyles } from '@/components/ui'
import { Space, usePalette } from '@/constants/theme'
import { useSession } from '@/providers/session'
import { notificationPermissionState, type NotificationPermissionState } from '@/hooks/notification-state'

function DiagnosticRow({ label, value }: { label: string; value: string }) {
  const palette = usePalette()
  return <View style={sharedStyles.split}><Text style={[sharedStyles.detail, { color: palette.muted }]}>{label}</Text><Text selectable style={[sharedStyles.body, styles.value, { color: palette.text }]}>{value}</Text></View>
}

export default function SettingsScreen() {
  const palette = usePalette()
  const session = useSession()
  const [notificationPermission, setNotificationPermission] = useState<NotificationPermissionState>({ label: 'NOT_DETERMINED', usable: false, canAskAgain: true })
  const [notificationError, setNotificationError] = useState<string | null>(null)
  const [notificationBusy, setNotificationBusy] = useState(false)
  const refreshNotifications = async () => {
    const value = await Notifications.getPermissionsAsync()
    setNotificationPermission(notificationPermissionState(value))
  }
  useEffect(() => {
    void refreshNotifications().catch(() => setNotificationError('Notification permission could not be read.'))
    const subscription = AppState.addEventListener('change', (next) => {
      if (next === 'active') void refreshNotifications().catch(() => setNotificationError('Notification permission could not be read.'))
    })
    return () => subscription.remove()
  }, [])
  const requestNotifications = async () => {
    if (notificationPermission.label === 'DENIED' && !notificationPermission.canAskAgain) {
      await Linking.openSettings()
      return
    }
    setNotificationBusy(true)
    setNotificationError(null)
    try {
      const value = notificationPermission.usable
        ? await Notifications.getPermissionsAsync()
        : await Notifications.requestPermissionsAsync({ ios: { allowAlert: true, allowBadge: true, allowSound: false } })
      const next = notificationPermissionState(value)
      setNotificationPermission(next)
      if (next.usable) await Notifications.scheduleNotificationAsync({ content: { title: 'Tessera test notification', body: 'Local notifications work on this device.', data: { url: '/settings' } }, trigger: null })
    } catch { setNotificationError('The notification test could not be completed.') }
    finally { setNotificationBusy(false) }
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
        <Button label="Test connection" icon="arrow.triangle.2.circlepath" onPress={() => void session.reconnect()} />
      </Card>
      <SectionTitle>Security</SectionTitle>
      <Card>
        <View style={sharedStyles.split}><View style={{ flex: 1 }}><Text style={[sharedStyles.title, { color: palette.text }]}>App lock</Text><Text style={[sharedStyles.detail, { color: palette.muted }]}>Require Face ID, Touch ID, or device passcode after Tessera leaves the foreground.</Text></View><Switch value={session.lockEnabled} onValueChange={(value) => void session.setLockEnabled(value)} trackColor={{ true: palette.accent }} /></View>
        <DiagnosticRow label="Signed in as" value={session.principal?.principal ?? 'Unknown'} />
        <DiagnosticRow label="Role" value={session.principal?.role ?? 'Unknown'} />
      </Card>
      <SectionTitle>Notifications</SectionTitle>
      <Card><View style={sharedStyles.split}><View style={{ flex: 1 }}><Text style={[sharedStyles.title, { color: palette.text }]}>Local notification test</Text><Text style={[sharedStyles.detail, { color: palette.muted }]}>Permission: {notificationPermission.label.replaceAll('_', ' ').toLowerCase()}</Text></View><Status value={notificationPermission.label} /></View>{notificationError ? <Text accessibilityRole="alert" style={[sharedStyles.detail, { color: palette.danger }]}>{notificationError}</Text> : null}<Button label={notificationPermission.label === 'DENIED' && !notificationPermission.canAskAgain ? 'Open notification settings' : notificationPermission.usable ? 'Send test notification' : 'Allow and send test'} icon={notificationPermission.label === 'DENIED' && !notificationPermission.canAskAgain ? 'gear' : 'bell.badge'} busy={notificationBusy} onPress={() => void requestNotifications()} /></Card>
      <SectionTitle>Session</SectionTitle>
      <Button label="Sign out" icon="rectangle.portrait.and.arrow.right" tone="danger" onPress={confirmSignOut} />
    </ScrollView>
  )
}
const styles = StyleSheet.create({ value: { flex: 1, textAlign: 'right' } })