import { useEffect, useState } from 'react'
import { Alert, RefreshControl, ScrollView, StyleSheet, Text, TextInput, View } from 'react-native'
import * as WebBrowser from 'expo-web-browser'
import type { Plugin } from '@tessera/client'

import { Button, Card, Empty, ErrorState, Loading, SectionTitle, Status, sharedStyles } from '@/components/ui'
import { Radius, Space, usePalette } from '@/constants/theme'
import type { IntegrationCatalogItem, IntegrationSource } from '@/lib/api'
import { useSession } from '@/providers/session'
import { trustedHttpsUrl } from '@/components/display-boundary'

export default function PluginsScreen() {
  const palette = usePalette()
  const { api } = useSession()
  const [items, setItems] = useState<Plugin[]>([])
  const [sources, setSources] = useState<IntegrationSource[]>([])
  const [results, setResults] = useState<IntegrationCatalogItem[]>([])
  const [searchText, setSearchText] = useState('')
  const [searchedFor, setSearchedFor] = useState('')
  const [loading, setLoading] = useState(true)
  const [refreshing, setRefreshing] = useState(false)
  const [searching, setSearching] = useState(false)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const load = async (refresh = false) => {
    refresh ? setRefreshing(true) : setLoading(true)
    setError(null)
    try {
      const [plugins, catalogSources] = await Promise.all([api.plugins(), api.integrationSources()])
      setItems(plugins.items)
      setSources(catalogSources.items)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Plugins unavailable') }
    finally { setLoading(false); setRefreshing(false) }
  }
  useEffect(() => { void load() }, [])
  const toggle = (item: Plugin) => Alert.alert(`${item.enabled ? 'Disable' : 'Enable'} ${item.name}?`, item.enabled ? 'New Chat and Job invocations will be blocked. Existing evidence and history remain.' : 'The plugin’s declared capabilities will become available under current grants.', [{ text: 'Cancel', style: 'cancel' }, { text: item.enabled ? 'Disable' : 'Enable', style: item.enabled ? 'destructive' : 'default', onPress: () => void (async () => { setBusy(item.id); try { await api.setPluginEnabled(item); await load(true) } catch (cause) { setError(cause instanceof Error ? cause.message : 'Update failed') } finally { setBusy(null) } })() }])
  const search = async () => {
    const query = searchText.trim()
    if (query.length < 2 || searching) return
    setSearching(true)
    setError(null)
    setSearchedFor(query)
    try {
      const found = await api.searchIntegrations(query)
      setResults(found.items)
      setSources(found.sources)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Integration search failed') }
    finally { setSearching(false) }
  }
  const inspect = async (item: IntegrationCatalogItem) => {
    if (!item.inspectUrl) return
    const url = trustedHttpsUrl(item.inspectUrl)
    if (!url) { setError('Integration source URL is not trusted'); return }
    await WebBrowser.openBrowserAsync(url, { presentationStyle: WebBrowser.WebBrowserPresentationStyle.PAGE_SHEET })
  }
  const install = (item: IntegrationCatalogItem) => Alert.alert(
    `Install ${item.name}?`,
    `Publisher: ${item.publisher}\nVersion: ${item.version}\nRuntime: ${item.runtime}\nTrust: ${item.trustLevel}\nSensitivity: ${item.sensitivity}\nCapabilities: ${item.capabilitiesSummary.join(' · ') || 'None declared'}\nAuthorization: ${item.authTypes.join(', ') || 'None'}\n\nThis exact package is already hash-validated in the reviewed Tessera server image. It will be installed disabled; enabling and account authorization remain separate.`,
    [
      { text: 'Cancel', style: 'cancel' },
      { text: 'Install disabled', onPress: () => void (async () => {
        setBusy(item.id)
        setError(null)
        try {
          await api.installReviewedIntegration(item)
          setResults((current) => current.map((value) => value.id === item.id && value.version === item.version ? { ...value, installed: true, installState: 'INSTALLED' } : value))
          await load(true)
        } catch (cause) { setError(cause instanceof Error ? cause.message : 'Installation failed') }
        finally { setBusy(null) }
      })() },
    ],
  )
  if (loading) return <View style={[sharedStyles.page, { backgroundColor: palette.background }]}><Loading /></View>
  if (error && !items.length) return <View style={[sharedStyles.page, { backgroundColor: palette.background }]}><ErrorState message={error} retry={() => void load()} /></View>
  return <ScrollView style={{ backgroundColor: palette.background }} contentContainerStyle={sharedStyles.listContent} refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void load(true)} />}>
    <SectionTitle detail="Hash-validated integrations running on your Tessera server">Installed</SectionTitle>
    {!items.length ? <Empty icon="shippingbox" title="No plugins installed" detail="Tessera discovers provider capabilities through its plugin and MCP runtime." /> : items.map((item) => <Card key={`${item.id}:${item.version}`}><View style={sharedStyles.split}><View style={{ flex: 1 }}><Text style={[sharedStyles.title, { color: palette.text }]}>{item.name}</Text><Text style={[sharedStyles.detail, { color: palette.muted }]}>{item.publisher} · {item.version}</Text></View><Status value={item.enabled ? 'Enabled' : 'Disabled'} /></View><Text style={[sharedStyles.detail, { color: palette.muted }]}>{item.capabilities.length} capabilities · {item.configurationState.replaceAll('_', ' ')}</Text><Button label={item.enabled ? 'Disable' : 'Enable'} icon={item.enabled ? 'pause.circle' : 'play.circle'} tone={item.enabled ? 'danger' : 'primary'} busy={busy === item.id} onPress={() => toggle(item)} /></Card>)}
    <SectionTitle detail="Official MCP Registry, public GitHub metadata, and installed integrations">Search</SectionTitle>
    <Card>
      <TextInput accessibilityLabel="Search integrations" value={searchText} onChangeText={setSearchText} placeholder="Search integrations…" placeholderTextColor={palette.muted} maxLength={100} returnKeyType="search" onSubmitEditing={() => void search()} style={[styles.searchInput, { color: palette.text, borderColor: palette.line, backgroundColor: palette.elevated }]} />
      <Button label={searching ? 'Searching…' : 'Search'} icon="magnifyingglass" tone="primary" busy={searching} disabled={searchText.trim().length < 2} onPress={() => void search()} />
      <View style={styles.sourceList}>{sources.map((source) => <Status key={source.id} value={`${source.name}: ${source.state}`} />)}</View>
      <Text style={[sharedStyles.detail, { color: palette.muted }]}>Search results are metadata only. Tessera never downloads or executes unreviewed code on this phone or server.</Text>
    </Card>
    {error ? <Text accessibilityRole="alert" style={[sharedStyles.body, { color: palette.danger }]}>{error}</Text> : null}
    {searchedFor && !searching && !results.length ? <Text style={[sharedStyles.body, { color: palette.muted, textAlign: 'center', paddingVertical: Space.xl }]}>No compatible integrations matched “{searchedFor}”.</Text> : null}
    {results.map((item) => <Card key={`${item.source}:${item.id}:${item.version}`}><View style={sharedStyles.split}><View style={{ flex: 1 }}><Text style={[sharedStyles.title, { color: palette.text }]}>{item.name}</Text><Text style={[sharedStyles.detail, { color: palette.muted }]}>{item.source} · {item.publisher} · {item.runtime}</Text></View><Status value={item.installState} /></View><Text style={[sharedStyles.body, { color: palette.text }]}>{item.description}</Text><View style={styles.sourceList}><Status value={item.trustLevel} /><Status value={item.sensitivity} /></View>{item.authTypes.length ? <Text style={[sharedStyles.detail, { color: palette.warning }]}>Authorization: {item.authTypes.join(', ')}. Review where credentials and sensitive data would be sent.</Text> : null}{item.inspectUrl ? <Button label="Inspect source" icon="safari" onPress={() => void inspect(item)} /> : <Text style={[sharedStyles.detail, { color: palette.muted }]}>{item.source === 'local' ? 'Built into the reviewed Tessera server image.' : 'Public source URL unavailable.'}</Text>}{item.source === 'local' && !item.installed ? <Button label={busy === item.id ? 'Installing…' : 'Review installation'} icon="shippingbox" tone="primary" busy={busy === item.id} onPress={() => install(item)} /> : !item.installed ? <Text style={[sharedStyles.detail, { color: palette.muted }]}>A reviewed server package is required before installation.</Text> : null}</Card>)}
  </ScrollView>
}

const styles = StyleSheet.create({
  searchInput: { minHeight: 44, borderWidth: StyleSheet.hairlineWidth, borderRadius: Radius.sm, paddingHorizontal: Space.md, fontSize: 16 },
  sourceList: { flexDirection: 'row', flexWrap: 'wrap', gap: Space.sm },
})