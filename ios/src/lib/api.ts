import {
  createHttpClient,
  createRemoteApi,
  type Account,
  type Action,
  type Activity,
  type Conversation,
  type DevelopmentTask,
  type DevelopmentWorkspace,
  type Job,
  type JobRun,
  type Memory,
  type MemoryWhy,
  type Message,
  type ModelProfile,
  type Page,
  type Plugin,
  type RealtimeNegotiation,
  type RealtimeTurnInput,
  type RealtimeTurnReceipt,
  type RealtimeToolCallResult,
  type RealtimeVoiceStatus,
  type Settings,
  type AuthLease,
  type RouteManager,
  type RemoteHostArtifactDetailDto,
  type RemoteHostDetailDto,
  type RemoteHostRunProjectionDto,
  type RemoteHostSummaryDto,
} from '@tessera/client'

type HttpClient = ReturnType<typeof createHttpClient>
export type ExecutionEvent = { id: string | null; type: string; data: unknown }
export type ReginaMariaConnector = { id: string; displayName: string }
export type SetupIntegration = { id: string; name: string; state: string; runtimeState: string; accountId: string | null; accountHealth: string | null; detailCode: string | null; connectPath: string | null }
export type SetupStatus = { server: { state: string; displayName: string; version: string }; ai: { state: string; gatewayId: string | null; displayName: string | null; model: string | null; profileId: string | null; detailCode: string | null }; integrations: SetupIntegration[]; canOpenChat: boolean; requiredActionCount: number }
export type IntegrationCatalogItem = { id: string; name: string; description: string; source: string; publisher: string; runtime: string; repositoryOrPackage: string | null; version: string; license: string | null; trustLevel: string; capabilitiesSummary: string[]; authTypes: string[]; sensitivity: string; installationMode: string; installState: string; installed: boolean; inspectUrl: string | null }
export type IntegrationSource = { id: string; name: string; state: string; errorCode: string | null }
export type IntegrationSearch = { items: IntegrationCatalogItem[]; sources: IntegrationSource[] }
export type IntegrationInstallReceipt = { pluginId: string; version: string; installState: 'INSTALLED' }

export class TesseraApi {
  private readonly remote

  constructor(private readonly http: HttpClient, private readonly routes: RouteManager, private readonly auth: () => Promise<AuthLease>) {
    this.remote = createRemoteApi(http)
  }

  setupStatus = () => this.http.request<SetupStatus>('/setup/status')
  bootstrapSetup = () => this.http.mutate<SetupStatus>('/setup/bootstrap', 'POST', {}, 'setup-bootstrap')
  integrationSources = () => this.http.request<{ items: IntegrationSource[] }>('/integrations/sources')
  searchIntegrations = (query: string, limit = 20) => this.http.request<IntegrationSearch>(`/integrations/search?query=${encodeURIComponent(query)}&limit=${limit}`)
  installReviewedIntegration = (item: IntegrationCatalogItem) => this.http.mutate<IntegrationInstallReceipt>(`/integrations/local/${encodeURIComponent(item.id)}/versions/${encodeURIComponent(item.version)}/install`, 'POST', {}, 'integration-install')
  conversations = () => this.http.request<Page<Conversation>>('/conversations')
  createConversation = (modelProfileId: string | null) => this.http.mutate<Conversation>('/conversations', 'POST', { title: 'New conversation', modelProfileId }, 'conversation')
  messages = (id: string) => this.http.request<Page<Message>>(`/conversations/${encodeURIComponent(id)}/messages`)
  realtimeVoiceStatus = () => this.http.request<RealtimeVoiceStatus>('/realtime-voice/status')
  negotiateRealtimeVoice = (conversationId: string, clientAttemptId: string, offerSdp: string) => this.http.mutate<RealtimeNegotiation>(`/conversations/${encodeURIComponent(conversationId)}/realtime-sessions`, 'POST', { clientAttemptId, offerSdp }, 'realtime-negotiation')
  saveRealtimeTurn = (conversationId: string, sessionId: string, input: RealtimeTurnInput) => this.http.mutate<RealtimeTurnReceipt>(`/conversations/${encodeURIComponent(conversationId)}/realtime-sessions/${encodeURIComponent(sessionId)}/turns`, 'POST', input, 'realtime-turn')
  invokeRealtimeTool = (conversationId: string, sessionId: string, clientCallId: string, name: string, args: Record<string, unknown>, idempotencyKey?: string) => this.http.mutate<RealtimeToolCallResult>(`/conversations/${encodeURIComponent(conversationId)}/realtime-sessions/${encodeURIComponent(sessionId)}/tool-calls`, 'POST', { clientCallId, name, arguments: args }, 'realtime-tool', idempotencyKey)
  endRealtimeVoice = (conversationId: string, sessionId: string, reason: string) => this.http.mutate<{ id: string; resourceType: string; version: number }>(`/conversations/${encodeURIComponent(conversationId)}/realtime-sessions/${encodeURIComponent(sessionId)}/end`, 'POST', { reason }, 'realtime-end')
  developmentWorkspaces = (id: string) => this.http.request<Page<DevelopmentWorkspace>>(`/conversations/${encodeURIComponent(id)}/development-workspaces`)
  createDevelopmentTask = (conversationId: string, input: { name: string; workspaceId: string; commandProfile: 'repository.status'; arguments: string[] }) => this.http.mutate<DevelopmentTask>(`/conversations/${encodeURIComponent(conversationId)}/development-tasks`, 'POST', input, 'development-task')
  sendMessage = (id: string, modelProfileId: string, text: string) => this.http.mutate<{ messageId: string; executionId: string; replayed: boolean }>(`/conversations/${encodeURIComponent(id)}/messages`, 'POST', { text, modelProfileId }, 'message')
  modelProfiles = () => this.http.request<Page<ModelProfile>>('/settings/model-profiles')
  settings = () => this.http.request<Settings>('/settings')
  jobs = () => this.http.request<Page<Job>>('/jobs')
  job = (id: string) => this.http.request<Job>(`/jobs/${encodeURIComponent(id)}`)
  runJob = (item: Job) => this.http.mutate<JobRun>(`/jobs/${encodeURIComponent(item.id)}/run`, 'POST', { expectedVersion: item.version }, 'job-run')
  setJobState = (item: Job, operation: 'pause' | 'resume') => this.http.mutate<Job>(`/jobs/${encodeURIComponent(item.id)}/${operation}`, 'POST', { expectedVersion: item.version })
  jobRun = (runId: string) => this.http.request<{ run: JobRun; outputs: Page<{ outputRef: string; kind: string; mediaType: string; summary: string; text: string | null; truncated: boolean; createdAt: string }> }>(`/job-runs/${encodeURIComponent(runId)}`)
  accounts = () => this.http.request<Page<Account>>('/accounts')
  beginGmailOAuth = (displayName: string) => this.http.mutate<{ authorizeUrl: string }>('/accounts/gmail/connect', 'POST', { displayName })
  beginOneDriveOAuth = (displayName: string) => this.http.mutate<{ authorizeUrl: string }>('/accounts/onedrive/connect', 'POST', { displayName })
  reginaMariaConnectors = () => this.http.request<{ items: ReginaMariaConnector[] }>('/accounts/regina-maria/connectors')
  connectReginaMaria = (connectorId: string, displayName: string) => this.http.mutate<Account>('/accounts/regina-maria/connect', 'POST', { connectorId, displayName })
  validateAccount = (item: Account) => this.http.mutate<Account>(`/accounts/${encodeURIComponent(item.id)}/validate`, 'POST', { expectedVersion: item.version }, 'account-validate')
  disableAccount = (item: Account) => this.http.mutate<Account>(`/accounts/${encodeURIComponent(item.id)}/disable`, 'POST', { expectedVersion: item.version })
  plugins = () => this.http.request<Page<Plugin>>('/plugins')
  setPluginEnabled = (item: Plugin) => this.http.mutate<Plugin>(`/plugins/${encodeURIComponent(item.id)}/versions/${encodeURIComponent(item.version)}/${item.enabled ? 'disable' : 'enable'}`, 'POST', { expectedVersion: item.versionStamp })
  memory = () => this.http.request<Page<Memory>>('/memory')
  memoryWhy = (id: string) => this.http.request<MemoryWhy>(`/memory/${encodeURIComponent(id)}/why`)
  stopUsingMemory = (item: Memory) => this.http.mutate<Memory>(`/memory/${encodeURIComponent(item.assertionId)}/stop-using`, 'POST', { expectedVersion: item.version }, 'memory-stop')
  activity = () => this.http.request<Page<Activity>>('/activity')
  actions = (query = '') => this.http.request<Page<Action>>(`/actions${query}`)
  action = (id: string) => this.http.request<Action>(`/actions/${encodeURIComponent(id)}`)
  approveAction = (item: Action) => this.http.mutate<Action>(`/actions/${encodeURIComponent(item.id)}/approve`, 'POST', { expectedVersion: item.version }, 'approval')
  cancelAction = (item: Action) => this.http.mutate<Action>(`/actions/${encodeURIComponent(item.id)}/cancel`, 'POST', { expectedVersion: item.version })
  remoteHosts = (): Promise<Page<RemoteHostSummaryDto>> => this.remote.hosts()
  remoteHost = (hostId: string): Promise<RemoteHostDetailDto> => this.remote.host(hostId)
  revokeRemoteHost = (host: RemoteHostSummaryDto): Promise<RemoteHostDetailDto> => this.remote.revokeHost(host)
  remoteRunProjection = (runId: string): Promise<RemoteHostRunProjectionDto> => this.remote.runProjection(runId)
  remoteArtifact = (artifactId: string): Promise<RemoteHostArtifactDetailDto> => this.remote.artifact(artifactId)

  async watchExecution(conversationId: string, executionId: string, signal: AbortSignal, onEvent: (event: ExecutionEvent) => void) {
    const seen = new Set<string>()
    let after = 0
    for (let attempt = 0; attempt < 2; attempt += 1) {
      try {
        let terminal = false
        let authLease: AuthLease | undefined
        const query = new URLSearchParams({ executionId })
        if (after > 0) query.set('after', String(after))
        const response = await this.routes.requestAuthenticated(`/api/v1/conversations/${encodeURIComponent(conversationId)}/events?${query}`, { signal }, async () => {
          authLease = await this.auth()
          return authLease
        })
        if (!response.ok || !response.body) throw new Error('event_stream_unavailable')
        const reader = response.body.getReader()
        const decoder = new TextDecoder()
        let pending = ''
        const emit = () => {
          while (true) {
            const match = /\r?\n\r?\n/.exec(pending)
            if (match?.index === undefined) return
            const block = pending.slice(0, match.index)
            pending = pending.slice(match.index + match[0].length)
            if (block.length > 256 * 1024) throw new Error('event_too_large')
            let id: string | null = null
            let type = 'message'
            const data: string[] = []
            for (const line of block.split(/\r?\n/)) {
              if (line.startsWith('id:')) id = line.slice(3).trim()
              if (line.startsWith('event:')) type = line.slice(6).trim()
              if (line.startsWith('data:')) data.push(line.slice(5).trimStart())
            }
            if (!data.length || (id && seen.has(id))) continue
            if (id) {
              seen.add(id)
              if (/^[0-9]+$/.test(id)) after = Math.max(after, Number(id))
            }
            const raw = data.join('\n')
            if (type === 'completed' || type === 'failure') terminal = true
            if (!authLease?.isCurrent()) throw new Error('session_invalidated')
            let value: unknown = raw
            try { value = JSON.parse(raw) } catch { /* provider-safe text status */ }
            onEvent({ id, type, data: value })
            if (!authLease?.isCurrent()) throw new Error('session_invalidated')
          }
        }
        while (true) {
          const chunk = await reader.read()
          if (!authLease?.isCurrent()) throw new Error('session_invalidated')
          if (chunk.done) break
          pending += decoder.decode(chunk.value, { stream: true })
          if (pending.length > 1024 * 1024) {
            await reader.cancel()
            throw new Error('event_stream_buffer_exceeded')
          }
          emit()
        }
        pending += decoder.decode()
        emit()
        if (!authLease?.isCurrent()) throw new Error('session_invalidated')
        if (terminal) return
        throw new Error('event_stream_ended')
      } catch (error) {
        if (signal.aborted || attempt === 1) throw error
        await this.routes.ensureConnected()
      }
    }
  }
}