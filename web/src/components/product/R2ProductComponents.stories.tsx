import type { Meta, StoryObj } from '@storybook/react-vite'
import { ActionApprovalCard, JobRunTimeline } from './R2ProductComponents'

const action = {
  id: 'action-1', conversationId: 'conversation-1', messageId: 'message-1', jobId: null, jobRunId: null,
  pluginId: 'github', pluginVersion: '1.0.0', capabilityId: 'github.issues.create', capabilityVersion: '1',
  accountId: 'account-work', target: 'owner/sandbox', payloadPreview: { title: 'Review R2 alpha', body: 'Created after exact approval.' },
  state: 'PROPOSED', expiresAt: '2026-08-10T18:00:00Z', providerReceipt: null, verificationState: null,
  failureCode: null, version: 0,
}

const meta = {
  title: 'Product/ActionApprovalCard',
  component: ActionApprovalCard,
  parameters: { layout: 'padded', a11y: { test: 'error' } },
} satisfies Meta<typeof ActionApprovalCard>
export default meta

type Story = StoryObj<typeof meta>
export const Proposed: Story = { args: { action, accountLabel: 'Work GitHub' } }
export const Running: Story = { args: { action: { ...action, state: 'STARTED', version: 2 } } }
export const Verified: Story = { args: { action: { ...action, state: 'EXTERNALLY_CONFIRMED', providerReceipt: 'github-issue-42', verificationState: 'provider_verified', version: 5 } } }
export const ReconciliationRequired: Story = { args: { action: { ...action, state: 'RECONCILIATION_REQUIRED', failureCode: 'provider_timeout', version: 3 } } }

export const JobWaitingForApproval: StoryObj<typeof JobRunTimeline> = {
  render: () => <JobRunTimeline detail={{
    run: { id: 'run-1', runId: 'run-1', jobId: 'job-1', scheduledFor: '2026-08-10T17:00:00Z', state: 'WAITING_FOR_APPROVAL', startedAt: '2026-08-10T17:00:01Z', endedAt: null, modelProfileId: 'profile-1', contextSnapshotRef: null, capabilityCallIds: [], accountIds: ['account-work'], actionIds: ['action-1'], outputRefs: [], evidenceRefs: [], errorCode: null, version: 2 },
    contextSnapshot: null, capabilityUses: { items: [], nextCursor: null }, accountUses: { items: [], nextCursor: null },
    actions: { items: [{ ...action, jobId: 'job-1', jobRunId: 'run-1' }], nextCursor: null }, outputs: { items: [], nextCursor: null }, evidence: { items: [], nextCursor: null },
    trace: { items: [{ sequence: 1, occurredAt: '2026-08-10T17:00:01Z', type: 'awaiting_user_approval', summary: 'Waiting for exact user approval', actionId: 'action-1', errorCode: null }], nextCursor: null },
  }} />,
}

export const DevelopmentOutput: StoryObj<typeof JobRunTimeline> = {
  render: () => <JobRunTimeline job={{
    id: 'job-dev', jobId: 'job-dev', name: 'Repository status: Tessera', instruction: 'Development command profile: repository.status',
    desiredState: 'ACTIVE', health: 'READY', modelProfileId: null,
    schedule: { kind: 'once', at: '2026-08-12T18:00:00Z', localTime: null, timeZone: 'UTC', days: null }, nextOccurrence: null,
    accountGrants: [], capabilityGrants: [], sideEffectGrants: [], contextPolicy: {}, lastRun: null,
    kind: 'DEVELOPMENT', conversationId: 'conversation-1', developmentSpec: { workspaceId: 'workspace-1', commandProfile: 'repository.status', arguments: [], effect: 'READ_ONLY', timeoutSeconds: 300, outputLimitBytes: 32768 }, version: 1,
  }} detail={{
    run: { id: 'run-dev', runId: 'run-dev', jobId: 'job-dev', scheduledFor: '2026-08-12T18:00:00Z', state: 'SUCCEEDED', startedAt: '2026-08-12T18:00:01Z', endedAt: '2026-08-12T18:00:02Z', modelProfileId: null, contextSnapshotRef: null, capabilityCallIds: [], accountIds: [], actionIds: [], outputRefs: ['output:run-dev:log'], evidenceRefs: [], errorCode: null, version: 3 },
    contextSnapshot: null, capabilityUses: { items: [], nextCursor: null }, accountUses: { items: [], nextCursor: null }, actions: { items: [], nextCursor: null }, evidence: { items: [], nextCursor: null }, trace: { items: [{ sequence: 1, occurredAt: '2026-08-12T18:00:02Z', type: 'development_completed', summary: 'Repository status completed', actionId: null, errorCode: null }], nextCursor: null },
    outputs: { items: [{ outputRef: 'output:run-dev:log', runId: 'run-dev', kind: 'DEVELOPMENT_LOG', mediaType: 'text/plain; charset=utf-8', summary: 'Development command log', text: '## main\n M src/Tessera.Broker/R2SchedulerService.cs', truncated: true, createdAt: '2026-08-12T18:00:02Z' }], nextCursor: null },
  }} />,
}
