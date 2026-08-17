import { useEffect, useMemo, useState } from 'react'
import { Alert, Pressable, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native'
import { router, useLocalSearchParams } from 'expo-router'
import { TesseraProblem, type Action, type Job, type RemoteHostArtifactDetailDto, type RemoteHostDetailDto, type RemoteHostRunProjectionDto } from '@tessera/client'

import { Button, Card, ErrorState, Loading, SectionTitle, Status, formatTime, sharedStyles } from '@/components/ui'
import { Space, usePalette } from '@/constants/theme'
import { useSession } from '@/providers/session'
import { boundedDisplayText } from '@/components/display-boundary'

type DetailState = { host: RemoteHostDetailDto; job: Job | null; projection: RemoteHostRunProjectionDto | null; actions: Action[] }

export default function RemoteHostScreen() {
  const palette = usePalette()
  const { api } = useSession()
  const { id = '' } = useLocalSearchParams<{ id: string }>()
  const [detail, setDetail] = useState<DetailState | null>(null)
  const [artifact, setArtifact] = useState<RemoteHostArtifactDetailDto | null>(null)
  const [loading, setLoading] = useState(true)
  const [refreshing, setRefreshing] = useState(false)
  const [artifactLoading, setArtifactLoading] = useState<string | null>(null)
  const [revoking, setRevoking] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = async (refresh = false) => {
    refresh ? setRefreshing(true) : setLoading(true)
    setError(null)
    try {
      const [host, jobs, actions] = await Promise.all([api.remoteHost(id), api.jobs(), api.actions('?approvalRequired=true')])
      const jobsWithRuns = jobs.items.filter((item) => item.lastRun)
      const projections = await Promise.allSettled(jobsWithRuns.map((item) => api.remoteRunProjection(item.lastRun!.id)))
      const assignment = projections.flatMap((result, index) => result.status === 'fulfilled' && result.value.host?.hostId === id ? [{ job: jobsWithRuns[index], projection: result.value }] : [])[0]
      const job = assignment?.job ?? null
      setDetail({ host, job, projection: assignment?.projection ?? null, actions: actions.items.filter((item) => item.jobRunId === job?.lastRun?.id) })
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Remote Host unavailable') }
    finally { setLoading(false); setRefreshing(false) }
  }
  useEffect(() => { void load() }, [id])

  const pendingActions = useMemo(() => detail?.actions.filter((item) => item.state === 'PROPOSED') ?? [], [detail?.actions])
  const loadArtifact = async (artifactId: string) => {
    setArtifactLoading(artifactId)
    setError(null)
    try { setArtifact(await api.remoteArtifact(artifactId)) }
    catch (cause) { setError(cause instanceof Error ? cause.message : 'Artifact unavailable') }
    finally { setArtifactLoading(null) }
  }
  const confirmRevoke = () => {
    const current = detail
    if (!current || current.host.host.lifecycle === 'REVOKED' || revoking) return
    Alert.alert(
      `Revoke ${current.host.host.displayName}?`,
      `Host revision ${current.host.host.version}. New work will stop using this Mac. Historical Jobs, Actions, Evidence, artifacts, and Activity remain available.`,
      [
        { text: 'Keep Host', style: 'cancel' },
        {
          text: 'Revoke Host',
          style: 'destructive',
          onPress: () => void (async () => {
            setRevoking(true)
            setError(null)
            try {
              const host = await api.revokeRemoteHost(current.host.host)
              setDetail((current) => current ? { ...current, host } : current)
            } catch (cause) {
              if (cause instanceof TesseraProblem && cause.status === 409) {
                await load(true)
                setError('This Host changed. Review the refreshed revision before revoking it.')
              } else setError(cause instanceof Error ? cause.message : 'Host could not be revoked')
            }
            finally { setRevoking(false) }
          })(),
        },
      ],
    )
  }
  if (loading && !detail) return <View style={[sharedStyles.page, { backgroundColor: palette.background }]}><Loading /></View>
  if (error && !detail) return <View style={[sharedStyles.page, { backgroundColor: palette.background }]}><ErrorState message={error} retry={() => void load()} /></View>
  if (!detail) return null
  const activeCapabilities = detail.host.capabilityGrants.filter((item) => item.revokedAt === null)
  const activeResources = detail.host.resourceGrants.filter((item) => item.revokedAt === null)
  const artifactDisplay = artifact ? boundedDisplayText(artifact.textContent, 'No content available.') : null
  return (
    <ScrollView style={{ backgroundColor: palette.background }} contentContainerStyle={sharedStyles.listContent} refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void load(true)} />}>
      <Card><View style={sharedStyles.split}><View style={{ flex: 1 }}><Text accessibilityRole="header" style={[sharedStyles.title, { color: palette.text }]}>{detail.host.host.displayName}</Text><Text style={[sharedStyles.detail, { color: palette.muted }]}>{detail.host.host.platform} · {detail.host.host.architecture}</Text></View><Status value={detail.host.host.lifecycle} /></View><Text style={[sharedStyles.detail, { color: palette.muted }]}>Last seen {formatTime(detail.host.host.lastSeenAt)} · {detail.host.host.connectionStatus.replaceAll('_', ' ')}</Text></Card>

      <SectionTitle detail="Canonical Job and blocker state">Current work</SectionTitle>
      <Card>
        {detail.projection?.blocker ? <><Status value={detail.projection.blocker.code} /><Text accessibilityRole="alert" style={[sharedStyles.body, { color: palette.warning }]}>{detail.projection.blocker.detailCode?.replaceAll('_', ' ') ?? 'Waiting for Remote Host recovery.'}</Text></> : null}
        {detail.job?.lastRun ? <><Text style={[sharedStyles.title, { color: palette.text }]}>{detail.job.name}</Text><Text style={[sharedStyles.detail, { color: palette.muted }]}>{detail.job.lastRun.state.replaceAll('_', ' ')} · scheduled {formatTime(detail.job.lastRun.scheduledFor)}</Text><Button label="View canonical run" icon="arrow.up.right.square" onPress={() => router.push(`/job-run/${detail.job!.lastRun!.id}?jobId=${detail.job!.id}` as never)} /></> : <Text style={[sharedStyles.body, { color: palette.muted }]}>No Job is assigned to this Host.</Text>}
      </Card>

      {pendingActions.length ? <><SectionTitle detail="Approvals remain in the existing Action screen">Action required</SectionTitle>{pendingActions.map((item) => <Pressable key={item.id} accessibilityRole="button" accessibilityLabel={`Review ${item.capabilityId}`} onPress={() => router.push(`/action/${item.id}`)}><Card><View style={sharedStyles.split}><Text style={[sharedStyles.title, { color: palette.text, flex: 1 }]}>{item.capabilityId}</Text><Status value={item.state} /></View><Text style={[sharedStyles.detail, { color: palette.muted }]}>{item.target}</Text></Card></Pressable>)}</> : null}

      <SectionTitle>Durable progress</SectionTitle>
      <Card>{detail.projection?.checkpoints.length ? detail.projection.checkpoints.map((item) => <View key={item.sequence} style={[styles.row, { borderBottomColor: palette.line }]}><Text style={[sharedStyles.body, { color: palette.text }]}>{item.step.replaceAll('_', ' ')}</Text><Text style={[sharedStyles.detail, { color: palette.muted }]}>{formatTime(item.createdAt)}</Text></View>) : <Text style={[sharedStyles.body, { color: palette.muted }]}>No checkpoints recorded.</Text>}</Card>

      <SectionTitle detail="Untrusted plain text loads only when requested">Artifacts</SectionTitle>
      <Card>{detail.projection?.artifacts.length ? detail.projection.artifacts.map((item) => <View key={item.artifactId} style={[styles.row, { borderBottomColor: palette.line }]}><View style={sharedStyles.split}><View style={{ flex: 1 }}><Text style={[sharedStyles.title, { color: palette.text }]}>{item.summary}</Text><Text style={[sharedStyles.detail, { color: palette.muted }]}>{item.sizeBytes} bytes · {item.contentState}{item.truncated ? ' · truncated' : ''}</Text></View><Button label="Preview" busy={artifactLoading === item.artifactId} disabled={item.contentState === 'EXPIRED'} onPress={() => void loadArtifact(item.artifactId)} /></View></View>) : <Text style={[sharedStyles.body, { color: palette.muted }]}>No artifacts retained.</Text>}{artifactDisplay ? <><Text selectable accessibilityLabel="Untrusted artifact plain text" style={[styles.artifact, { color: palette.text, backgroundColor: palette.elevated, borderColor: palette.line }]}>{artifactDisplay.text}</Text>{artifactDisplay.truncated ? <Text style={[sharedStyles.detail, { color: palette.warning }]}>Artifact truncated at the client display limit.</Text> : null}</> : null}</Card>

      <SectionTitle>Granted access</SectionTitle>
      <Card><Text style={[sharedStyles.title, { color: palette.text }]}>Capabilities</Text>{activeCapabilities.map((item) => <Text key={`${item.capabilityId}:${item.capabilityVersion}`} style={[sharedStyles.body, { color: palette.text }]}>{item.capabilityId} · v{item.capabilityVersion}</Text>)}{!activeCapabilities.length ? <Text style={[sharedStyles.detail, { color: palette.muted }]}>No capabilities granted.</Text> : null}<Text style={[sharedStyles.title, { color: palette.text }]}>Resources</Text>{activeResources.map((item) => { const source = detail.host.resources.find((resource) => resource.resourceId === item.resourceId); return <Text key={item.resourceId} style={[sharedStyles.body, { color: palette.text }]}>{source?.displayName ?? item.resourceId} · {item.accessMode}</Text> })}{!activeResources.length ? <Text style={[sharedStyles.detail, { color: palette.muted }]}>No resources granted.</Text> : null}</Card>
      <Button label={detail.host.host.lifecycle === 'REVOKED' ? 'Host revoked' : 'Revoke Host'} icon="xmark.shield.fill" tone="danger" busy={revoking} disabled={detail.host.host.lifecycle === 'REVOKED'} onPress={confirmRevoke} />
      {error ? <Text accessibilityRole="alert" style={[sharedStyles.body, { color: palette.danger }]}>{error}</Text> : null}
    </ScrollView>
  )
}

const styles = StyleSheet.create({ row: { borderBottomWidth: StyleSheet.hairlineWidth, paddingBottom: Space.md, marginBottom: Space.md, gap: Space.xs }, artifact: { borderWidth: StyleSheet.hairlineWidth, borderRadius: 6, padding: Space.md, fontFamily: 'Menlo', fontSize: 12, lineHeight: 18 } })