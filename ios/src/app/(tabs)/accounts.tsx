import { useEffect, useState } from 'react'
import { Alert, FlatList, RefreshControl, StyleSheet, Text, View } from 'react-native'
import * as WebBrowser from 'expo-web-browser'
import type { Account } from '@tessera/client'

import { Button, Card, Empty, ErrorState, Loading, SectionTitle, Status, formatTime, sharedStyles } from '@/components/ui'
import { Space, usePalette } from '@/constants/theme'
import type { ReginaMariaConnector, SetupIntegration } from '@/lib/api'
import { useSession } from '@/providers/session'

export default function AccountsScreen() {
  const palette = usePalette()
  const { api } = useSession()
  const [items, setItems] = useState<Account[]>([])
  const [connectors, setConnectors] = useState<ReginaMariaConnector[]>([])
  const [readiness, setReadiness] = useState<SetupIntegration[]>([])
  const [loading, setLoading] = useState(true)
  const [refreshing, setRefreshing] = useState(false)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const load = async (refresh = false) => {
    refresh ? setRefreshing(true) : setLoading(true)
    setError(null)
    try {
      const [accounts, availableConnectors, setup] = await Promise.all([api.accounts(), api.reginaMariaConnectors().catch(() => ({ items: [] })), api.setupStatus()])
      setItems(accounts.items)
      setConnectors(availableConnectors.items)
      setReadiness(setup.integrations)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Accounts unavailable') }
    finally { setLoading(false); setRefreshing(false) }
  }
  useEffect(() => { void load() }, [])

  const validate = async (item: Account) => {
    setBusy(item.id)
    try { await api.validateAccount(item); await load(true) } catch (cause) { setError(cause instanceof Error ? cause.message : 'Validation failed') }
    finally { setBusy(null) }
  }
  const confirmDisable = (item: Account) => Alert.alert('Disable account?', `${item.displayName} will no longer be available to Chat or Jobs. Existing history stays intact.`, [
    { text: 'Keep enabled', style: 'cancel' },
    { text: 'Disable', style: 'destructive', onPress: () => void (async () => { setBusy(item.id); try { await api.disableAccount(item); await load(true) } catch (cause) { setError(cause instanceof Error ? cause.message : 'Disable failed') } finally { setBusy(null) } })() },
  ])
  const connectGmail = async () => {
    setBusy('gmail-connect')
    setError(null)
    try {
      const result = await api.beginGmailOAuth('Gmail')
      const url = new URL(result.authorizeUrl)
      if (url.protocol !== 'https:') throw new Error('oauth_url_invalid')
      await WebBrowser.openBrowserAsync(url.toString(), { presentationStyle: WebBrowser.WebBrowserPresentationStyle.PAGE_SHEET })
      await load(true)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Gmail connection failed') }
    finally { setBusy(null) }
  }
  const connectRm = (connector: ReginaMariaConnector) => Alert.alert('Connect Regina Maria?', `Connect the separately authorized profile “${connector.displayName}” to your Tessera account?`, [
    { text: 'Cancel', style: 'cancel' },
    { text: 'Connect', onPress: () => void (async () => { setBusy(`rm:${connector.id}`); try { await api.connectReginaMaria(connector.id, connector.displayName); await load(true) } catch (cause) { setError(cause instanceof Error ? cause.message : 'Connection failed') } finally { setBusy(null) } })() },
  ])

  if (loading) return <View style={[sharedStyles.page, { backgroundColor: palette.background }]}><Loading /></View>
  if (error && !items.length) return <View style={[sharedStyles.page, { backgroundColor: palette.background }]}><ErrorState message={error} retry={() => void load()} /></View>
  return (
    <View style={[sharedStyles.page, { backgroundColor: palette.background }]}>
      <FlatList data={items} keyExtractor={(item) => item.id} contentContainerStyle={[sharedStyles.listContent, !items.length && styles.empty]} refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void load(true)} />} ListHeaderComponent={<View style={{ gap: Space.sm }}><SectionTitle detail="Installed runtime and account authorization are separate">Readiness</SectionTitle>{readiness.map((integration) => <Card key={integration.id}><View style={sharedStyles.split}><View style={{ flex: 1 }}><Text style={[sharedStyles.title, { color: palette.text }]}>{integration.name}</Text><Text style={[sharedStyles.detail, { color: palette.muted }]}>{integration.state === 'CONNECTED' ? 'Account connected' : integration.runtimeState === 'READY' ? 'Runtime ready; account authorization remains' : integration.detailCode?.replaceAll('_', ' ') ?? 'Unavailable'}</Text></View><Status value={integration.state} /></View></Card>)}<SectionTitle detail="Authorization stays with each provider">Connect</SectionTitle><Button label="Connect Gmail" icon="envelope.badge" tone="primary" busy={busy === 'gmail-connect'} disabled={Boolean(busy)} onPress={() => void connectGmail()} />{connectors.map((connector) => <Button key={connector.id} label={`Connect ${connector.displayName}`} icon="cross.case" busy={busy === `rm:${connector.id}`} disabled={Boolean(busy)} onPress={() => connectRm(connector)} />)}<SectionTitle>Connected accounts</SectionTitle></View>} ListEmptyComponent={<Empty icon="person.crop.circle.badge.plus" title="No connected accounts" detail="Connect Gmail above, or ask the account holder to authorize a Regina Maria profile before connecting it." />} renderItem={({ item }) => (
        <Card>
          <View style={sharedStyles.split}><View style={{ flex: 1 }}><Text style={[sharedStyles.title, { color: palette.text }]}>{item.displayName}</Text><Text style={[sharedStyles.detail, { color: palette.muted }]}>{item.pluginId} · {item.lifecycle.replaceAll('_', ' ')}</Text></View><Status value={item.health} /></View>
          {item.identityHint ? <Text style={[sharedStyles.body, { color: palette.text }]}>{item.identityHint}</Text> : null}
          <Text style={[sharedStyles.detail, { color: palette.muted }]}>Last verified: {formatTime(item.lastSuccessfulUse)}</Text>
          <View style={sharedStyles.actions}><Button label="Validate" icon="checkmark.shield" busy={busy === item.id} disabled={Boolean(busy)} onPress={() => void validate(item)} />{item.lifecycle !== 'DISABLED' ? <Button label="Disable" icon="pause.circle" tone="danger" disabled={Boolean(busy)} onPress={() => confirmDisable(item)} /> : null}</View>
        </Card>
      )} />
      {error ? <Text style={[styles.error, { color: palette.danger }]}>{error}</Text> : null}
    </View>
  )
}
const styles = StyleSheet.create({ empty: { flexGrow: 1 }, error: { padding: Space.lg, fontSize: 13 } })