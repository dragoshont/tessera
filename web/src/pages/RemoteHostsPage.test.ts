import { describe, expect, it } from 'vitest'
import type { Action, Job, RemoteApi, RemoteHostDetailDto, RemoteHostSummaryDto } from '@tessera/client'
import { loadRemoteInventory } from '../lib/remote-inventory'

const host: RemoteHostSummaryDto = {
  hostId: 'host-main', displayName: 'Home Mac', platform: 'macOS', architecture: 'arm64',
  lifecycle: 'BUSY', connectionStatus: 'CONNECTED', agentVersion: '1.0.0', protocolVersion: '1',
  lastSeenAt: '2026-08-14T12:00:00Z', pairedAt: '2026-08-14T11:00:00Z', revokedAt: null, version: 3,
}
const detail: RemoteHostDetailDto = {
  host,
  capabilities: [{ capabilityId: 'host.repo.identity', capabilityVersion: '1', schemaHash: 'a'.repeat(64), sideEffectClass: 'READ_ONLY', advertisedAt: '2026-08-14T11:00:00Z' }],
  capabilityGrants: [{ capabilityId: 'host.repo.identity', capabilityVersion: '1', grantedAt: '2026-08-14T11:00:00Z', revokedAt: null, version: 1 }],
  resources: [{ resourceId: 'repo-main', type: 'REPOSITORY', displayName: 'Tessera', fingerprint: 'b'.repeat(64), state: 'AVAILABLE', advertisedAt: '2026-08-14T11:00:00Z', version: 1 }],
  resourceGrants: [{ resourceId: 'repo-main', accessMode: 'READ_ONLY', grantedAt: '2026-08-14T11:00:00Z', revokedAt: null, version: 1 }],
}
const run = { id: 'run-main', runId: 'run-main', jobId: 'job-main', scheduledFor: '2026-08-14T12:00:00Z', state: 'WAITING_FOR_APPROVAL', startedAt: null, endedAt: null, modelProfileId: null, contextSnapshotRef: null, capabilityCallIds: [], accountIds: [], actionIds: [], outputRefs: [], evidenceRefs: [], errorCode: null, version: 2 }
const job = { id: 'job-main', jobId: 'job-main', name: 'Inspect repository', instruction: 'Read identity', desiredState: 'ACTIVE', health: 'WAITING', modelProfileId: null, schedule: { kind: 'once', at: null, localTime: null, timeZone: 'UTC', days: null }, nextOccurrence: null, accountGrants: [], capabilityGrants: [], sideEffectGrants: [], contextPolicy: {}, lastRun: run, kind: 'DEVELOPMENT', conversationId: 'conversation-1', developmentSpec: null, version: 4 } satisfies Job
const action = { id: 'action-1', conversationId: null, messageId: null, jobId: job.id, jobRunId: run.id, pluginId: 'github', pluginVersion: '1', capabilityId: 'github.issues.create', capabilityVersion: '1', accountId: null, target: 'owner/repo', payloadPreview: {}, state: 'PROPOSED', expiresAt: null, providerReceipt: null, verificationState: null, failureCode: null, version: 1 } satisfies Action

function remote(overrides: Partial<RemoteApi> = {}): RemoteApi {
  return {
    hosts: async () => ({ items: [host], nextCursor: null }),
    host: async () => detail,
    runProjection: async () => ({ blocker: null, lease: null, host, checkpoints: [], artifacts: [] }),
    ...overrides,
  } as RemoteApi
}

describe('Remote inventory binding', () => {
  it('joins canonical grants, JobRun projection, and pending Action by server identity', async () => {
    const result = await loadRemoteInventory(remote(), {
      jobs: async () => ({ items: [job], nextCursor: null }),
      actions: async () => ({ items: [action], nextCursor: null }),
    })

    expect(result.partial).toBe(false)
    expect(result.records[0].host).toMatchObject({ hostId: host.hostId, capabilityCount: 1, resourceCount: 1, currentJob: { runId: run.id, state: run.state, pendingApprovals: 1 } })
    expect(result.records[0].pendingActions).toEqual([action])
  })

  it('keeps canonical host inventory visible when enrichment fails', async () => {
    const result = await loadRemoteInventory(remote({ host: async () => { throw new Error('detail unavailable') } }), {
      jobs: async () => { throw new Error('jobs unavailable') },
      actions: async () => ({ items: [], nextCursor: null }),
    })

    expect(result.partial).toBe(true)
    expect(result.records).toHaveLength(1)
    expect(result.records[0].host).toMatchObject({ hostId: host.hostId, capabilityCount: 0, resourceCount: 0, currentJob: null })
  })
})