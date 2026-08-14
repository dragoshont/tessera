import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, userEvent, within } from 'storybook/test'
import { ActionApprovalCard } from '../product/R2ProductComponents'
import { RemoteHostDetail, type RemoteArtifact, type RemoteHostDetailProps } from './RemoteWorkspace'
import { remoteHostsData } from './RemoteWorkspace.stories'
import { withDarkRemote } from './RemoteStoryDecorators'

const busyHost = remoteHostsData[0]
const idleHost = { ...busyHost, lifecycle: 'ONLINE' as const, currentJob: null }
const offlineHost = { ...remoteHostsData[1], lifecycle: 'OFFLINE' as const }
const revokedHost = { ...idleHost, lifecycle: 'REVOKED' as const }
const updateHost = { ...remoteHostsData[2], lifecycle: 'UPDATE_REQUIRED' as const }

const checkpoints = [
  { sequence: 1, step: 'JOB_ACCEPTED', summary: 'Host accepted the fenced lease', occurredAt: '2026-08-14T12:50:10Z' },
  { sequence: 2, step: 'STEP_STARTED', summary: 'Reading descriptor-bound repository identity', occurredAt: '2026-08-14T12:50:12Z' },
  { sequence: 3, step: 'STEP_COMPLETED', summary: 'Repository identity captured', occurredAt: '2026-08-14T12:50:13Z' },
]

const artifact: RemoteArtifact = {
  artifactId: 'artifact-repository-identity',
  summary: 'Repository identity',
  kind: 'TEXT',
  mediaType: 'text/plain',
  sizeBytes: 112,
  sha256: 'b30157c25c3bcfb7dc0e5d95f2a93a236ce45b148b909f67924fbdc6fa290001',
  retention: 'RUN',
  createdAt: '2026-08-14T12:50:13Z',
  redacted: true,
  truncated: false,
  contentState: 'AVAILABLE',
  textContent: 'branch=2.0-beta\ncommit=e78fc50e3de5b8fab349a684f26ee475ac554dc9\nresource=[REDACTED]',
}

const common: Pick<RemoteHostDetailProps, 'checkpoints' | 'capabilities' | 'resources' | 'activity'> = {
  checkpoints,
  capabilities: [{ id: 'host.repo.identity@1', label: 'Repository identity', detail: 'Version 1 · read only' }],
  resources: [{ id: 'repo-tessera', label: 'Tessera repository', detail: 'Opaque resource · read only' }],
  activity: [
    { id: 'activity-1', summary: 'Host connected', occurredAt: '2026-08-14T12:49:55Z' },
    { id: 'activity-2', summary: 'Lease accepted', occurredAt: '2026-08-14T12:50:10Z' },
  ],
}

const action = {
  id: 'action-remote-1', conversationId: null, messageId: null, jobId: 'job-repository', jobRunId: 'run-repository-identity',
  pluginId: 'github', pluginVersion: '1.0.0', capabilityId: 'github.issues.create', capabilityVersion: '1',
  accountId: 'account-work', target: 'owner/sandbox', payloadPreview: { title: 'Review Remote result' },
  state: 'PROPOSED', expiresAt: '2026-08-14T13:10:00Z', providerReceipt: null, verificationState: null,
  failureCode: null, version: 1,
}

const meta = {
  title: 'Product/RemoteHostDetail',
  component: RemoteHostDetail,
  parameters: { layout: 'padded', a11y: { test: 'error' } },
} satisfies Meta<typeof RemoteHostDetail>

export default meta
type Story = StoryObj<typeof meta>

export const OnlineIdle: Story = { args: { state: 'online-idle', host: idleHost, ...common } }
export const BusyRunning: Story = {
  args: { state: 'busy-running', host: busyHost, currentJob: busyHost.currentJob, ...common },
  play: async ({ canvasElement }) => {
    const page = within(canvasElement.ownerDocument.body)
    const trigger = page.getByRole('button', { name: 'Cancel Job' })
    await userEvent.click(trigger)
    const dialog = await page.findByRole('dialog', { name: /Cancel Inspect Tessera repository identity/ })
    await expect(within(dialog).getByRole('button', { name: 'Keep Job running' })).toHaveFocus()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Keep Job running' }))
    await expect(trigger).toHaveFocus()
  },
}
export const OfflineWaitingForHost: Story = { args: { state: 'offline-waiting-for-host', host: offlineHost, currentJob: offlineHost.currentJob, blocker: 'Waiting for Home Mac mini to reconnect. The Job remains queued.', ...common } }
export const UpdateRequired: Story = { args: { state: 'update-required', host: updateHost, blocker: 'This Host must be updated before it can accept work.', ...common } }
export const Revoked: Story = { args: { state: 'revoked', host: revokedHost, artifacts: [artifact], ...common } }
export const ApprovalRequired: Story = {
  args: {
    state: 'approval-required',
    host: busyHost,
    currentJob: { ...busyHost.currentJob!, state: 'WAITING_FOR_APPROVAL', pendingApprovals: 1 },
    approval: <ActionApprovalCard action={action} accountLabel="Work GitHub" />,
    ...common,
  },
}
export const Canceling: Story = { args: { state: 'canceling', host: busyHost, currentJob: { ...busyHost.currentJob!, state: 'CANCEL_REQUESTED' }, announcement: 'Cancel requested. Waiting for a safe checkpoint.', ...common } }
export const SucceededWithArtifacts: Story = { args: { state: 'succeeded-with-artifacts', host: idleHost, currentJob: { ...busyHost.currentJob!, state: 'SUCCEEDED' }, artifacts: [artifact], ...common } }
export const TruncatedArtifact: Story = { args: { state: 'truncated-artifact', host: idleHost, currentJob: { ...busyHost.currentJob!, state: 'SUCCEEDED' }, artifacts: [{ ...artifact, truncated: true, sizeBytes: 262144 }], ...common } }
export const TruncatedArtifactDark: Story = { args: TruncatedArtifact.args, decorators: [withDarkRemote] }
export const ExpiredArtifact: Story = { args: { state: 'expired-artifact', host: idleHost, currentJob: { ...busyHost.currentJob!, state: 'SUCCEEDED' }, artifacts: [{ ...artifact, contentState: 'EXPIRED', textContent: null, expiresAt: '2026-08-15T12:50:13Z' }], ...common } }