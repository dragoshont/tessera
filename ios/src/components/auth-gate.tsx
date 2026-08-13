import { type PropsWithChildren, useState } from 'react'
import { ActivityIndicator, ScrollView, StyleSheet, Text, View } from 'react-native'
import { SafeAreaView } from 'react-native-safe-area-context'

import { Button, Card, Mark, Status, sharedStyles } from '@/components/ui'
import { Space, usePalette } from '@/constants/theme'
import { useSession } from '@/providers/session'

export function AuthGate({ children }: PropsWithChildren) {
  const palette = usePalette()
  const session = useSession()
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  if (session.status === 'authenticated' && !session.locked) return children

  const run = async (operation: () => Promise<void>) => {
    setBusy(true)
    setError(null)
    try { await operation() } catch (cause) { setError(cause instanceof Error ? cause.message.replaceAll('_', ' ') : 'Request failed') }
    finally { setBusy(false) }
  }

  return (
    <SafeAreaView style={[styles.page, { backgroundColor: palette.background }]}>
      <ScrollView contentContainerStyle={styles.content} alwaysBounceVertical={false}>
        <Mark size={54} />
        <View style={styles.heading}><Text maxFontSizeMultiplier={1.4} style={[styles.name, { color: palette.text }]}>Tessera</Text><Text maxFontSizeMultiplier={2} style={[sharedStyles.body, { color: palette.muted, textAlign: 'center' }]}>Your private assistant, connected to one trusted home server.</Text></View>
        {session.status === 'booting' ? <ActivityIndicator color={palette.accent} /> : session.locked ? (
          <Card style={styles.panel}><Status value="Locked" /><Text maxFontSizeMultiplier={2} style={[sharedStyles.body, { color: palette.muted }]}>Unlock to restore your secured session.</Text><Button label="Unlock Tessera" icon="lock.open" tone="primary" busy={busy} onPress={() => void run(session.unlock)} /><Button label="Sign out" icon="rectangle.portrait.and.arrow.right" tone="danger" disabled={busy} onPress={() => void run(session.signOut)} /></Card>
        ) : session.status === 'anonymous' ? (
          <Card style={styles.panel}><Status value="Server verified" /><Text maxFontSizeMultiplier={2} style={[sharedStyles.title, { color: palette.text }]}>{session.descriptor?.displayName}</Text><Text maxFontSizeMultiplier={2} style={[sharedStyles.detail, { color: palette.muted }]}>Sign in through the system browser. Tessera stores the resulting session in Keychain.</Text><Button label="Sign in" icon="person.crop.circle.badge.checkmark" tone="primary" busy={busy} onPress={() => void run(session.signIn)} /></Card>
        ) : (
          <Card style={styles.panel}><Status value="Offline" /><Text maxFontSizeMultiplier={2} style={[sharedStyles.title, { color: palette.text }]}>Trusted server unavailable</Text><Text maxFontSizeMultiplier={2} style={[sharedStyles.detail, { color: palette.muted }]}>Tessera will not sign in or send data until TLS and the expected server identity both verify.</Text><Text maxFontSizeMultiplier={2} style={[sharedStyles.detail, { color: palette.muted }]}>Reason: {session.diagnostics.failureCode?.replaceAll('_', ' ') ?? 'No verified route'}</Text><Button label="Retry connection" icon="arrow.clockwise" busy={busy} onPress={() => void run(session.reconnect)} /></Card>
        )}
        {error ? <Text accessibilityRole="alert" style={[styles.error, { color: palette.danger }]}>{error}</Text> : null}
      </ScrollView>
    </SafeAreaView>
  )
}

const styles = StyleSheet.create({
  page: { flex: 1 },
  content: { flexGrow: 1, justifyContent: 'center', alignItems: 'center', padding: Space.xl, gap: Space.xl },
  heading: { alignItems: 'center', gap: Space.sm, maxWidth: 350 },
  name: { fontSize: 34, lineHeight: 40, fontWeight: '700' },
  panel: { width: '100%', maxWidth: 420 },
  error: { fontSize: 13, textAlign: 'center' },
})