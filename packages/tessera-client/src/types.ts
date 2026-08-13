export type Page<T> = { items: T[]; nextCursor: string | null }
export type ProblemBody = { type?: string; title?: string; status?: number; detail?: string; instance?: string; code?: string; traceId?: string }

export type ServerDescriptor = {
  product: 'tessera'
  serverId: string
  displayName: string
  serverVersion: string
  apiVersion: 'v1'
  protocolVersion: 1
}

export type RouteKind = 'LOCAL' | 'REMOTE'
export type ConnectionState = 'CONNECTED' | 'DEGRADED' | 'OFFLINE'
export type ConnectionDiagnostics = {
  state: ConnectionState
  route: RouteKind | null
  latencyMs: number | null
  serverId: string | null
  serverVersion: string | null
  clientVersion: string
  lastSuccessfulConnection: string | null
  failureCode: string | null
}

export type Conversation = { id: string; conversationId: string; title: string; state: 'ACTIVE' | 'ARCHIVED' | 'DELETED'; modelProfileId: string | null; createdAt: string; updatedAt: string; version: number }
export type MessagePart = { id: string; kind: 'TEXT' | 'STATUS' | 'CAPABILITY_CALL' | 'CAPABILITY_RESULT' | 'ACTION' | 'EVIDENCE' | 'FAILURE'; text: string | null; capabilityCallId: string | null; capabilityResultId: string | null; actionId: string | null; evidenceRefs: string[]; errorCode: string | null }
export type Message = { id: string; messageId: string; conversationId: string; role: 'USER' | 'ASSISTANT' | 'SYSTEM_EVENT' | 'CAPABILITY'; status: string; parts: MessagePart[]; createdAt: string; completedAt: string | null; retryOf: string | null; version: number }
export type ModelProfile = { profileId: string; accountId: string; adapterKind: string; endpoint: string; model: string; contextLimit: number; enabled: boolean; streamingSupported: boolean; toolSupport: boolean; version: number }
export type Account = { id: string; accountId: string; providerId: string; pluginId: string; displayName: string; providerAccountId: string | null; identityHint: string | null; lifecycle: string; permissions: string[]; providerScopes: string[]; capabilityIds: string[]; health: string; lastSuccessfulUse: string | null; version: number }
export type Capability = { id: string; version: string; pluginId: string; description: string; accountRequired: boolean; requiredPermissions: string[]; sideEffectClass: string; available: boolean; blockedCode: string | null }
export type Plugin = { id: string; pluginId: string; name: string; version: string; pluginVersion: string; publisher: string; enabled: boolean; packageHash: string; configurationState: 'ACCOUNT_SCOPED' | 'NOT_REQUIRED'; accountProviderIds: string[]; capabilities: Capability[]; versionStamp: number }
export type Action = { id: string; conversationId: string | null; messageId: string | null; jobId: string | null; jobRunId: string | null; pluginId: string; pluginVersion: string; capabilityId: string; capabilityVersion: string; accountId: string | null; target: string; payloadPreview: unknown; state: string; expiresAt: string | null; providerReceipt: string | null; verificationState: string | null; failureCode: string | null; version: number }
export type JobSchedule = { kind: 'run-now' | 'once' | 'daily' | 'weekday'; at: string | null; localTime: string | null; timeZone: string; days: number[] | null }
export type JobRun = { id: string; runId: string; jobId: string; scheduledFor: string; state: string; startedAt: string | null; endedAt: string | null; modelProfileId: string | null; contextSnapshotRef: string | null; capabilityCallIds: string[]; accountIds: string[]; actionIds: string[]; outputRefs: string[]; evidenceRefs: string[]; errorCode: string | null; version: number }
export type DevelopmentSpec = { workspaceId: string; commandProfile: 'repository.status'; arguments: string[]; effect: 'READ_ONLY'; timeoutSeconds: number; outputLimitBytes: number }
export type DevelopmentWorkspace = { id: string; displayName: string; snapshotHash: string; state: 'READY'; createdAt: string; version: number }
export type DevelopmentTask = { job: Job; run: JobRun }
export type Job = { id: string; jobId: string; name: string; instruction: string; desiredState: string; health: string; modelProfileId: string | null; schedule: JobSchedule; nextOccurrence: string | null; accountGrants: string[]; capabilityGrants: string[]; sideEffectGrants: string[]; contextPolicy: unknown; lastRun: JobRun | null; kind: 'AUTOMATION' | 'DEVELOPMENT'; conversationId: string | null; developmentSpec: DevelopmentSpec | null; version: number }
export type Memory = { assertionId: string; subjectKey: string; predicate: string; value: string; status: string; validFrom: string; validTo: string | null; evidenceRefs: string[]; version: number }
export type MemoryWhy = { assertionId: string; current: Memory; previous: Memory | null; evidence: Array<{ evidenceId: string; sourceType: string; sourceLocator: string; observedAt: string; sourceTimestamp: string | null; boundedExcerpt: string | null }>; lineageRefs: string[] }
export type Activity = { id: string; kind: string; occurredAt: string; summary: string; state: string | null; resourceType: string; resourceId: string; evidenceRefs: string[] }
export type Settings = { defaultChatModelProfileId: string | null; defaultLightweightModelProfileId: string | null; timezone: string; approvalDefaults: unknown; memoryControls: unknown; version: number }