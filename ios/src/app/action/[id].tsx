import { useEffect, useState } from 'react'
import { Alert, ScrollView, StyleSheet, Text, View } from 'react-native'
import { useLocalSearchParams, router } from 'expo-router'
import type { Action } from '@tessera/client'

import { Button, Card, ErrorState, Loading, SectionTitle, Status, formatTime, sharedStyles } from '@/components/ui'
import { Space, usePalette } from '@/constants/theme'
import { useSession } from '@/providers/session'
import { actionReviewState, formatActionPreview } from '@/app/action/action-review-state'

export default function ActionReviewScreen() {
  const palette = usePalette()
  const { id } = useLocalSearchParams<{ id: string }>()
  const { api } = useSession()
  const [item, setItem] = useState<Action | null>(null)
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const load = async () => { setLoading(true); setError(null); try { setItem(await api.action(id)) } catch (cause) { setError(cause instanceof Error ? cause.message : 'Action unavailable') } finally { setLoading(false) } }
  useEffect(() => { void load() }, [id])

  const approve = () => {
    if (!item) return
    if (actionReviewState(item.state, item.expiresAt) !== 'PROPOSED') {
      setError('This action expired. Refresh it before making a decision.')
      void load()
      return
    }
    Alert.alert('Approve this exact action?', 'Tessera will authorize only the capability, account, target, and payload shown here. Approval is one-use and expires.', [{ text: 'Not now', style: 'cancel' }, { text: 'Approve and run', onPress: () => void decide('approve') }])
  }
  const cancel = () => item && Alert.alert('Cancel action?', 'The proposed operation will not run. Its audit history remains.', [{ text: 'Keep for review', style: 'cancel' }, { text: 'Cancel action', style: 'destructive', onPress: () => void decide('cancel') }])
  const decide = async (decision: 'approve' | 'cancel') => {
    if (!item) return
    if (decision === 'approve' && actionReviewState(item.state, item.expiresAt) !== 'PROPOSED') {
      setError('This action expired before approval. Review the refreshed action.')
      await load()
      return
    }
    setBusy(true)
    setError(null)
    try { setItem(decision === 'approve' ? await api.approveAction(item) : await api.cancelAction(item)) } catch (cause) { setError(cause instanceof Error ? cause.message : 'Decision failed') }
    finally { setBusy(false) }
  }

  if (loading) return <View style={[sharedStyles.page, { backgroundColor: palette.background }]}><Loading /></View>
  if (error && !item) return <View style={[sharedStyles.page, { backgroundColor: palette.background }]}><ErrorState message={error} retry={() => void load()} /></View>
  if (!item) return null
  const preview = formatActionPreview(item.payloadPreview)
  const reviewState = actionReviewState(item.state, item.expiresAt)
  return (
    <ScrollView style={{ backgroundColor: palette.background }} contentContainerStyle={sharedStyles.listContent}>
      <Card><View style={sharedStyles.split}><Text style={[sharedStyles.title, { color: palette.text, flex: 1 }]}>{item.capabilityId}</Text><Status value={reviewState} /></View><Text style={[sharedStyles.detail, { color: palette.muted }]}>{item.pluginId} {item.pluginVersion} · capability {item.capabilityVersion}</Text></Card>
      <SectionTitle>Exact scope</SectionTitle>
      <Card><View><Text style={[sharedStyles.detail, { color: palette.muted }]}>Target</Text><Text selectable style={[sharedStyles.body, { color: palette.text }]}>{item.target}</Text></View><View><Text style={[sharedStyles.detail, { color: palette.muted }]}>Account</Text><Text selectable style={[sharedStyles.body, { color: palette.text }]}>{item.accountId ?? 'No account'}</Text></View><View><Text style={[sharedStyles.detail, { color: palette.muted }]}>Expires</Text><Text style={[sharedStyles.body, { color: palette.text }]}>{formatTime(item.expiresAt)}</Text></View></Card>
      <SectionTitle>Payload preview</SectionTitle>
      <Card><Text selectable style={[styles.code, { color: palette.text }]}>{preview.text}</Text>{preview.truncated ? <Text style={[sharedStyles.detail, { color: palette.warning }]}>Preview truncated to 16,000 characters.</Text> : null}</Card>
      {item.providerReceipt || item.verificationState || item.failureCode ? <><SectionTitle>Outcome</SectionTitle><Card>{item.verificationState ? <Status value={item.verificationState} /> : null}{item.providerReceipt ? <Text style={[sharedStyles.detail, { color: palette.muted }]}>Provider receipt: {item.providerReceipt}</Text> : null}{item.failureCode ? <Text style={[sharedStyles.body, { color: palette.danger }]}>{item.failureCode.replaceAll('_', ' ')}</Text> : null}</Card></> : null}
      {reviewState === 'PROPOSED' ? <View style={styles.actions}><Button label="Approve and run" icon="checkmark.shield.fill" tone="primary" busy={busy} onPress={approve} /><Button label="Cancel action" icon="xmark.circle" tone="danger" disabled={busy} onPress={cancel} /></View> : reviewState === 'EXPIRED' ? <Button label="Refresh action" icon="arrow.clockwise" busy={loading} onPress={() => void load()} /> : <Button label="Done" icon="checkmark" onPress={() => router.back()} />}
      {error ? <Text accessibilityRole="alert" style={[sharedStyles.body, { color: palette.danger }]}>{error}</Text> : null}
    </ScrollView>
  )
}
const styles = StyleSheet.create({ code: { fontFamily: 'ui-monospace', fontSize: 12, lineHeight: 18 }, actions: { gap: Space.sm } })