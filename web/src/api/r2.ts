import { authHeader } from '../app/auth'
import { apiUrl } from '../app/runtime'

export type R2Page<T> = { items: T[]; nextCursor: string | null }
export type R2ProblemBody = { type?: string; title?: string; status?: number; detail?: string; instance?: string; code?: string; traceId?: string }

export class R2Problem extends Error {
  readonly status: number
  readonly code: string
  readonly problem?: R2ProblemBody

  constructor(status: number, code: string, problem?: R2ProblemBody) {
    super(problem?.detail ?? problem?.title ?? code)
    this.name = 'R2Problem'
    this.status = status
    this.code = code
    this.problem = problem
  }
}

export type R2Conversation = { id: string; conversationId: string; title: string; state: 'ACTIVE' | 'ARCHIVED' | 'DELETED'; modelProfileId: string | null; createdAt: string; updatedAt: string; version: number }
export type R2ConversationGrants = { accountGrants: string[]; capabilityGrants: string[]; version: number }
export type R2MessagePart = { id: string; kind: 'TEXT' | 'STATUS' | 'CAPABILITY_CALL' | 'CAPABILITY_RESULT' | 'ACTION' | 'EVIDENCE' | 'FAILURE'; text: string | null; capabilityCallId: string | null; capabilityResultId: string | null; actionId: string | null; evidenceRefs: string[]; errorCode: string | null }
export type R2Message = { id: string; messageId: string; conversationId: string; role: 'USER' | 'ASSISTANT' | 'SYSTEM_EVENT' | 'CAPABILITY'; status: string; parts: R2MessagePart[]; createdAt: string; completedAt: string | null; retryOf: string | null; version: number }
export type R2ModelProfile = { profileId: string; accountId: string; adapterKind: string; endpoint: string; model: string; contextLimit: number; enabled: boolean; streamingSupported: boolean; toolSupport: boolean; version: number }
export type R2Account = { id: string; accountId: string; providerId: string; pluginId: string; displayName: string; providerAccountId: string | null; identityHint: string | null; lifecycle: string; permissions: string[]; providerScopes: string[]; capabilityIds: string[]; health: string; lastSuccessfulUse: string | null; version: number }
export type R2ReginaMariaConnector = { id: string; displayName: string }
export type R2ModelGateway = { id: string; displayName: string }
export type R2Capability = { id: string; version: string; pluginId: string; description: string; accountRequired: boolean; requiredPermissions: string[]; sideEffectClass: string; available: boolean; blockedCode: string | null }
export type R2Plugin = { id: string; pluginId: string; name: string; version: string; pluginVersion: string; publisher: string; enabled: boolean; packageHash: string; configurationState: 'ACCOUNT_SCOPED' | 'NOT_REQUIRED'; accountProviderIds: string[]; capabilities: R2Capability[]; versionStamp: number }
export type R2Action = { id: string; conversationId: string | null; messageId: string | null; jobId: string | null; jobRunId: string | null; pluginId: string; pluginVersion: string; capabilityId: string; capabilityVersion: string; accountId: string | null; target: string; payloadPreview: unknown; state: string; expiresAt: string | null; providerReceipt: string | null; verificationState: string | null; failureCode: string | null; version: number }
export type R2JobSchedule = { kind: 'run-now' | 'once' | 'daily' | 'weekday'; at: string | null; localTime: string | null; timeZone: string; days: number[] | null }
export type R2JobRun = { id: string; runId: string; jobId: string; scheduledFor: string; state: string; startedAt: string | null; endedAt: string | null; modelProfileId: string | null; contextSnapshotRef: string | null; capabilityCallIds: string[]; accountIds: string[]; actionIds: string[]; outputRefs: string[]; evidenceRefs: string[]; errorCode: string | null; version: number }
export type R2Job = { id: string; jobId: string; name: string; instruction: string; desiredState: string; health: string; modelProfileId: string | null; schedule: R2JobSchedule; nextOccurrence: string | null; accountGrants: string[]; capabilityGrants: string[]; sideEffectGrants: string[]; contextPolicy: unknown; lastRun: R2JobRun | null; version: number }
export type R2JobRunDetail = { run: R2JobRun; contextSnapshot: { snapshotRef: string } | null; capabilityUses: R2Page<{ callId: string; pluginId: string; capabilityId: string; capabilityVersion: string; accountId: string | null; state: string; createdAt: string; completedAt: string | null; errorCode: string | null }>; accountUses: R2Page<{ callId: string; accountId: string; capabilityId: string; state: string; createdAt: string }>; actions: R2Page<R2Action>; outputs: R2Page<{ outputRef: string; runId: string; kind: string; mediaType: string; summary: string; text: string | null; truncated: boolean; createdAt: string }>; evidence: R2Page<{ evidenceId: string; sourceType: string; sourceLocator: string; observedAt: string; boundedExcerpt: string | null }>; trace: R2Page<{ sequence: number; occurredAt: string; type: string; summary: string; actionId: string | null; errorCode: string | null }> }
export type R2Memory = { assertionId: string; subjectKey: string; predicate: string; value: string; status: string; validFrom: string; validTo: string | null; evidenceRefs: string[]; version: number }
export type R2MemoryWhy = { assertionId: string; current: R2Memory; previous: R2Memory | null; evidence: Array<{ evidenceId: string; sourceType: string; sourceLocator: string; observedAt: string; sourceTimestamp: string | null; boundedExcerpt: string | null }>; lineageRefs: string[] }
export type R2Activity = { id: string; kind: string; occurredAt: string; summary: string; state: string | null; resourceType: string; resourceId: string; evidenceRefs: string[] }
export type R2Settings = { defaultChatModelProfileId: string | null; defaultLightweightModelProfileId: string | null; timezone: string; approvalDefaults: unknown; memoryControls: unknown; version: number }
export type R2ExecutionEvent = { type: string; data: unknown }

const key = (prefix: string) => `${prefix}-${crypto.randomUUID()}`

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(apiUrl(`/api/v1${path}`), { ...init, headers: { ...authHeader(), ...(init?.body ? { 'Content-Type': 'application/json' } : {}), ...init?.headers } })
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as R2ProblemBody | null
    throw new R2Problem(response.status, problem?.code ?? problem?.title ?? `http_${response.status}`, problem ?? undefined)
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

const mutate = <T>(path: string, method: 'POST' | 'PATCH' | 'PUT' | 'DELETE', body: unknown, prefix?: string) => request<T>(path, { method, headers: prefix ? { 'Idempotency-Key': key(prefix) } : undefined, body: JSON.stringify(body) })

export const r2Api = {
  conversations: () => request<R2Page<R2Conversation>>('/conversations'),
  createConversation: (modelProfileId: string | null, title = 'New conversation') => mutate<R2Conversation>('/conversations', 'POST', { title, modelProfileId }, 'conversation'),
  updateConversation: (item: R2Conversation, input: { title?: string; state?: 'ACTIVE' | 'ARCHIVED' }) => mutate<R2Conversation>(`/conversations/${encodeURIComponent(item.id)}`, 'PATCH', { ...input, expectedVersion: item.version }),
  deleteConversation: (item: R2Conversation) => mutate<void>(`/conversations/${encodeURIComponent(item.id)}`, 'DELETE', { expectedVersion: item.version }),
  messages: (id: string) => request<R2Page<R2Message>>(`/conversations/${encodeURIComponent(id)}/messages`),
  activeExecution: (id: string) => request<{ executionId: string; userMessageId: string; modelProfileId: string } | undefined>(`/conversations/${encodeURIComponent(id)}/active-execution`),
  conversationGrants: (id: string) => request<R2ConversationGrants>(`/conversations/${encodeURIComponent(id)}/grants`),
  updateConversationGrants: (id: string, grants: R2ConversationGrants, accountGrants: string[], capabilityGrants: Array<{ id: string; version: string }>) => mutate<R2ConversationGrants>(`/conversations/${encodeURIComponent(id)}/grants`, 'PUT', { accountGrants, capabilityGrants, expectedVersion: grants.version }),
  sendMessage: (id: string, modelProfileId: string, text: string) => mutate<{ messageId: string; executionId: string; replayed: boolean }>(`/conversations/${encodeURIComponent(id)}/messages`, 'POST', { text, modelProfileId }, 'message'),
  retryMessage: (id: string, messageId: string) => mutate<{ messageId: string; executionId: string; replayed: boolean }>(`/conversations/${encodeURIComponent(id)}/retry`, 'POST', { messageId }, 'retry'),
  stopExecution: (id: string, executionId: string) => mutate(`/conversations/${encodeURIComponent(id)}/stop`, 'POST', { executionId }, 'stop'),
  watchExecution: async (id: string, executionId: string, signal: AbortSignal, onEvent: (event: R2ExecutionEvent) => void) => {
    const response=await fetch(apiUrl(`/api/v1/conversations/${encodeURIComponent(id)}/events?executionId=${encodeURIComponent(executionId)}`),{headers:authHeader(),signal})
    if(!response.ok||!response.body)throw new R2Problem(response.status,'event_stream_unavailable')
    const reader=response.body.getReader();const decoder=new TextDecoder();let pending=''
    const emit=()=>{while(true){const lf=pending.indexOf('\n\n');const crlf=pending.indexOf('\r\n\r\n');const index=lf<0?crlf:crlf<0?lf:Math.min(lf,crlf);if(index<0)return;const separator=index===crlf&&crlf>=0?4:2;const block=pending.slice(0,index);pending=pending.slice(index+separator);let type='message';const data:string[]=[];for(const line of block.split(/\r?\n/)){if(line.startsWith('event:'))type=line.slice(6).trim();else if(line.startsWith('data:'))data.push(line.slice(5).trimStart())}if(data.length){const raw=data.join('\n');let value:unknown=raw;try{value=JSON.parse(raw)}catch{/* non-JSON provider-safe status */}onEvent({type,data:value})}}}
    while(true){const chunk=await reader.read();if(chunk.done)break;pending+=decoder.decode(chunk.value,{stream:true});emit()}pending+=decoder.decode();emit()
  },

  modelProfiles: () => request<R2Page<R2ModelProfile>>('/settings/model-profiles'),
  settings: () => request<R2Settings>('/settings'),
  updateSettings: (settings: R2Settings, input: Partial<Omit<R2Settings, 'version'>>) => mutate<R2Settings>('/settings', 'PATCH', { ...input, expectedVersion: settings.version }),
  configureModel: async (endpoint: string, model: string, secretInput: string) => {
    const account = await mutate<R2Account>('/accounts', 'POST', { pluginId: 'model-provider', displayName: model, secretInput, nonSecretConfig: { endpoint, providerId: 'openai-compatible', pluginVersion: '1.0.0' } }, 'model-account')
    await mutate<R2Account>(`/accounts/${encodeURIComponent(account.id)}/validate`, 'POST', { expectedVersion: account.version }, 'model-validate')
    return mutate<R2ModelProfile>('/settings/model-profiles', 'POST', { accountId: account.id, adapterKind: endpoint.startsWith('http://127.0.0.1') || endpoint.startsWith('http://localhost') ? 'openai-compatible-local' : 'openai-compatible-remote', endpoint, model, contextLimit: 32768 }, 'model-profile')
  },
  modelGateways:()=>request<{items:R2ModelGateway[]}>('/settings/model-gateways'),
  configureModelGateway:(gatewayId:string,model:string,secretInput:string)=>mutate<R2ModelProfile>('/settings/model-gateways/connect','POST',{gatewayId,model,secretInput,contextLimit:32768}),

  accounts: () => request<R2Page<R2Account>>('/accounts'),
  beginGmailOAuth: (displayName: string) => mutate<{ authorizeUrl: string }>('/accounts/gmail/connect', 'POST', { displayName }),
  reginaMariaConnectors: () => request<{ items: R2ReginaMariaConnector[] }>('/accounts/regina-maria/connectors'),
  connectReginaMaria: (connectorId: string, displayName: string) => mutate<R2Account>('/accounts/regina-maria/connect', 'POST', { connectorId, displayName }),
  connectAccount: (input: { pluginId: string; displayName: string; secretInput: string; nonSecretConfig: unknown }) => mutate<R2Account>('/accounts', 'POST', input, 'account'),
  validateAccount: (item: R2Account) => mutate<R2Account>(`/accounts/${encodeURIComponent(item.id)}/validate`, 'POST', { expectedVersion: item.version }, 'account-validate'),
  disableAccount: (item: R2Account) => mutate<R2Account>(`/accounts/${encodeURIComponent(item.id)}/disable`, 'POST', { expectedVersion: item.version }),
  revokeAccount: (item: R2Account) => mutate<R2Account>(`/accounts/${encodeURIComponent(item.id)}?expectedVersion=${item.version}`, 'DELETE', {}),

  plugins: () => request<R2Page<R2Plugin>>('/plugins'),
  pluginConfiguration: (item: R2Plugin) => request<{ values: Record<string, string>; configured: boolean; version: number }>(`/plugins/${encodeURIComponent(item.id)}/versions/${encodeURIComponent(item.version)}/configuration`),
  configurePlugin: (item: R2Plugin, values: Record<string, string>) => mutate(`/plugins/${encodeURIComponent(item.id)}/versions/${encodeURIComponent(item.version)}/configuration`, 'PUT', { values, expectedVersion: item.versionStamp }),
  setPluginEnabled: (item: R2Plugin) => mutate(`/plugins/${encodeURIComponent(item.id)}/versions/${encodeURIComponent(item.version)}/${item.enabled ? 'disable' : 'enable'}`, 'POST', { expectedVersion: item.versionStamp }),
  removePlugin: (item: R2Plugin) => mutate<void>(`/plugins/${encodeURIComponent(item.id)}/versions/${encodeURIComponent(item.version)}`, 'DELETE', { expectedVersion: item.versionStamp }),
  capabilities: () => request<R2Page<R2Capability>>('/capabilities?includeUnavailable=true'),
  invokeCapability: (input: { capabilityId: string; capabilityVersion: string; pluginId: string; pluginVersion: string; accountId: string | null; target: string; input: unknown; conversationId?: string; messageId?: string }) => mutate<{ executionId: string; result: unknown; evidenceRefs: string[] }>(`/capabilities/${encodeURIComponent(input.capabilityId)}/invoke`, 'POST', input, 'capability'),

  jobs: () => request<R2Page<R2Job>>('/jobs'),
  createJob: (input: { name: string; instruction: string; desiredState: string; modelProfileId: string | null; schedule: R2JobSchedule; contextPolicy: unknown; accountGrants: string[]; capabilityGrants: Array<{ id: string; version: string }>; sideEffectGrants: string[] }) => mutate<R2Job>('/jobs', 'POST', input, 'job'),
  updateJob: (item: R2Job, input: Record<string, unknown>) => mutate<R2Job>(`/jobs/${encodeURIComponent(item.id)}`, 'PATCH', { ...input, expectedVersion: item.version }),
  cancelJob: (item: R2Job) => mutate<R2Job>(`/jobs/${encodeURIComponent(item.id)}`, 'DELETE', { expectedVersion: item.version }),
  runJob: (item: R2Job) => mutate<R2JobRun>(`/jobs/${encodeURIComponent(item.id)}/run`, 'POST', { expectedVersion: item.version }, 'job-run'),
  setJobState: (item: R2Job, operation: 'pause' | 'resume') => mutate<R2Job>(`/jobs/${encodeURIComponent(item.id)}/${operation}`, 'POST', { expectedVersion: item.version }),
  jobRuns: (jobId: string) => request<R2Page<R2JobRun>>(`/jobs/${encodeURIComponent(jobId)}/runs`),
  jobRun: (runId: string) => request<R2JobRunDetail>(`/job-runs/${encodeURIComponent(runId)}`),

  actions: (query = '') => request<R2Page<R2Action>>(`/actions${query}`),
  action: (id: string) => request<R2Action>(`/actions/${encodeURIComponent(id)}`),
  proposeAction: (input: { capabilityId: string; capabilityVersion: string; pluginId: string; pluginVersion: string; accountId: string | null; target: string; input: unknown; conversationId?: string; messageId?: string; jobId?: string; jobRunId?: string }) => mutate<R2Action>('/actions', 'POST', input, 'action'),
  approveAction: (item: R2Action) => mutate<R2Action>(`/actions/${encodeURIComponent(item.id)}/approve`, 'POST', { expectedVersion: item.version }, 'approval'),
  cancelAction: (item: R2Action) => mutate<R2Action>(`/actions/${encodeURIComponent(item.id)}/cancel`, 'POST', { expectedVersion: item.version }),

  memory: (query = '') => request<R2Page<R2Memory>>(`/memory${query}`),
  remember: (subjectKey: string, predicate: string, value: string, sourceMessageId = 'explicit-ui') => mutate<R2Memory>('/memory', 'POST', { subjectKey, predicate, value, sourceMessageId }, 'memory'),
  correctMemory: (item: R2Memory, value: string) => mutate<R2Memory>(`/memory/${encodeURIComponent(item.assertionId)}/correct`, 'POST', { value, expectedVersion: item.version, sourceMessageId: 'explicit-ui' }, 'memory-correct'),
  memoryHistory: (id: string) => request<R2Page<unknown>>(`/memory/${encodeURIComponent(id)}/history`),
  memoryWhy: (id: string) => request<R2MemoryWhy>(`/memory/${encodeURIComponent(id)}/why`),
  stopUsingMemory: (item: R2Memory) => mutate<R2Memory>(`/memory/${encodeURIComponent(item.assertionId)}/stop-using`, 'POST', { expectedVersion: item.version }, 'memory-stop'),
  followUps: () => request<R2Page<unknown>>('/memory/follow-ups'),
  activity: (query = '') => request<R2Page<R2Activity>>(`/activity${query}`),
}