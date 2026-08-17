import { useEffect, useRef, useState } from 'react'
import { RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native'
import { useLocalSearchParams } from 'expo-router'
import { GenerationFence, type Job, type JobRun } from '@tessera/client'

import { Card, ErrorState, Loading, SectionTitle, Status, formatTime, sharedStyles } from '@/components/ui'
import { Space, usePalette } from '@/constants/theme'
import { useSession } from '@/providers/session'
import { boundedDisplayText } from '@/components/display-boundary'

type RunDetail = { run: JobRun; outputs: { items: Array<{ outputRef: string; kind: string; summary: string; text: string | null; truncated: boolean; createdAt: string }> } }

export default function JobRunScreen() {
  const palette = usePalette()
  const { api } = useSession()
  const { id, jobId } = useLocalSearchParams<{ id: string; jobId: string }>()
  const [detail, setDetail] = useState<RunDetail | null>(null)
  const [job, setJob] = useState<Job | null>(null)
  const [loading, setLoading] = useState(true)
  const [refreshing, setRefreshing] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const fence = useRef(new GenerationFence())

  const load = async (refresh = false) => {
    refresh ? setRefreshing(true) : setLoading(true)
    setError(null)
    const result = await fence.current.runLatest(() => Promise.all([api.jobRun(id), api.job(jobId)]))
    if (!result.current) return
    if (result.error) setError(result.error instanceof Error ? result.error.message : 'Run unavailable')
    else { setDetail(result.value![0]); setJob(result.value![1]) }
    setLoading(false)
    setRefreshing(false)
  }

  useEffect(() => {
    void load()
    const interval = setInterval(() => { if (detail?.run.state === 'QUEUED' || detail?.run.state === 'RUNNING') void load(true) }, 2000)
    return () => { clearInterval(interval); fence.current.invalidate() }
  }, [id, jobId, detail?.run.state])

  if (loading && !detail) return <View style={[sharedStyles.page, { backgroundColor: palette.background }]}><Loading /></View>
  if (error && !detail) return <View style={[sharedStyles.page, { backgroundColor: palette.background }]}><ErrorState message={error} retry={() => void load()} /></View>
  return <ScrollView style={{ backgroundColor: palette.background }} contentContainerStyle={styles.content} refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void load(true)} />}>
    <View style={sharedStyles.split}><View style={{ flex: 1 }}><Text accessibilityRole="header" style={[sharedStyles.title, { color: palette.text }]}>{job?.name ?? 'Development run'}</Text><Text style={[sharedStyles.detail, { color: palette.muted }]}>Scheduled {formatTime(detail?.run.scheduledFor)}</Text></View>{detail ? <Status value={detail.run.state} /> : null}</View>
    {job?.developmentSpec ? <Card><SectionTitle detail="Read only">Repository status</SectionTitle><Text style={[styles.mono, { color: palette.text }]}>{job.developmentSpec.commandProfile}</Text><Text style={[sharedStyles.detail, { color: palette.muted }]}>Workspace {job.developmentSpec.workspaceId}</Text></Card> : null}
    {detail?.run.errorCode ? <Text accessibilityRole="alert" style={[styles.error, { color: palette.danger }]}>{detail.run.errorCode.replaceAll('_', ' ')}</Text> : null}
    {detail?.outputs.items.map((output) => { const display = boundedDisplayText(output.text, 'No output.'); return <View key={output.outputRef} style={[styles.output, { borderColor: palette.line }]}><View style={sharedStyles.split}><Text style={[sharedStyles.title, { color: palette.text, flex: 1 }]}>{output.kind === 'DEVELOPMENT_LOG' ? 'Repository status log' : output.summary}</Text><Text style={[sharedStyles.detail, { color: palette.muted }]}>{formatTime(output.createdAt)}</Text></View><Text selectable style={[styles.outputText, { backgroundColor: palette.elevated, borderColor: palette.line, color: palette.text }]}>{display.text}</Text>{output.truncated || display.truncated ? <Text style={[sharedStyles.detail, { color: palette.warning }]}>Output truncated at a safe display limit.</Text> : null}</View> })}
    {detail && detail.outputs.items.length === 0 ? <Text style={[sharedStyles.body, { color: palette.muted }]}>{detail.run.state === 'QUEUED' ? 'Tessera accepted this run. It has not started yet.' : detail.run.state === 'RUNNING' ? 'Repository status is running in an isolated server workspace.' : 'No output was recorded.'}</Text> : null}
    {error ? <Text accessibilityRole="alert" style={[styles.error, { color: palette.danger }]}>{error}</Text> : null}
  </ScrollView>
}

const styles = StyleSheet.create({
  content: { padding: Space.lg, paddingBottom: 80, gap: Space.lg },
  mono: { fontFamily: 'Menlo', fontSize: 13 },
  output: { borderTopWidth: StyleSheet.hairlineWidth, paddingTop: Space.lg, gap: Space.md },
  outputText: { borderWidth: StyleSheet.hairlineWidth, borderRadius: 6, padding: Space.md, fontFamily: 'Menlo', fontSize: 12, lineHeight: 18 },
  error: { fontSize: 13, lineHeight: 18 },
})