import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, userEvent, within } from 'storybook/test'
import { RemoteWorkspace, type RemoteHostSummary, type RemotePairingCandidate } from './RemoteWorkspace'
import { withDarkRemote } from './RemoteStoryDecorators'

export const pairingCandidateData: RemotePairingCandidate = {
  pairingId: 'pairing-macbook-pro',
  expiresAt: '2026-08-14T13:05:00Z',
  hostName: 'Dragos MacBook Pro',
  platform: 'macOS 15.6',
  architecture: 'arm64',
  protection: 'SECURE_ENCLAVE',
  agentVersion: '1.0.0',
  protocolVersion: '1',
  capabilities: [{ id: 'host.repo.identity@1', label: 'Repository identity', detail: 'Read branch, commit, and resource fingerprint only.' }],
  resources: [{ id: 'repo-tessera', label: 'Tessera repository', detail: 'Opaque repository resource · read only' }],
}

export const remoteHostsData: RemoteHostSummary[] = [
  {
    hostId: 'host-macbook-pro',
    href: '/remote/hosts/host-macbook-pro',
    displayName: 'Dragos MacBook Pro',
    platform: 'macOS 15.6',
    architecture: 'arm64',
    lifecycle: 'BUSY',
    agentVersion: '1.0.0',
    protocolVersion: '1',
    statusObservedAt: '2026-08-14T12:52:00Z',
    lastSeenAt: '2026-08-14T12:52:00Z',
    capabilityCount: 1,
    resourceCount: 1,
    currentJob: {
      runId: 'run-repository-identity',
      name: 'Inspect Tessera repository identity',
      state: 'RUNNING',
      href: '/jobs/job-repository/runs/run-repository-identity',
      checkpoint: 'Reading HEAD',
      pendingApprovals: 0,
    },
  },
  {
    hostId: 'host-mac-mini',
    href: '/remote/hosts/host-mac-mini',
    displayName: 'Home Mac mini',
    platform: 'macOS 15.6',
    architecture: 'arm64',
    lifecycle: 'OFFLINE',
    agentVersion: '1.0.0',
    protocolVersion: '1',
    statusObservedAt: '2026-08-14T09:11:00Z',
    lastSeenAt: '2026-08-14T09:11:00Z',
    capabilityCount: 1,
    resourceCount: 1,
    currentJob: {
      runId: 'run-waiting',
      name: 'Inspect release repository identity',
      state: 'QUEUED · WAITING_FOR_HOST',
      href: '/jobs/job-release/runs/run-waiting',
      pendingApprovals: 0,
    },
  },
  {
    hostId: 'host-old-mac',
    href: '/remote/hosts/host-old-mac',
    displayName: 'Office Mac',
    platform: 'macOS 14.7',
    architecture: 'x86_64',
    lifecycle: 'UPDATE_REQUIRED',
    agentVersion: '0.9.0',
    protocolVersion: '1',
    statusObservedAt: '2026-08-13T17:32:00Z',
    lastSeenAt: '2026-08-13T17:32:00Z',
    capabilityCount: 1,
    resourceCount: 1,
    currentJob: null,
  },
]

const meta = {
  title: 'Product/RemoteWorkspace',
  component: RemoteWorkspace,
  excludeStories: /.*Data$/,
  parameters: { layout: 'padded', a11y: { test: 'error' } },
} satisfies Meta<typeof RemoteWorkspace>

export default meta
type Story = StoryObj<typeof meta>

export const Unsupported: Story = { args: { mode: 'unsupported' } }
export const Loading: Story = { args: { mode: 'loading' } }
export const ZeroHosts: Story = { args: { mode: 'zero-hosts' } }
export const PairingCodeEntry: Story = {
  args: { mode: 'pairing-code', pairingCandidate: pairingCandidateData },
  play: async ({ canvasElement }) => {
    const page = within(canvasElement.ownerDocument.body)
    const continueButton = await page.findByRole('button', { name: 'Continue' })
    await expect(continueButton).toBeDisabled()
    await userEvent.type(page.getByLabelText('Six-digit pairing code'), '123456')
    await expect(continueButton).toBeEnabled()
  },
}
export const PairingReview: Story = { args: { mode: 'pairing-review', pairingCandidate: pairingCandidateData } }
export const PairingReviewDark: Story = { args: PairingReview.args, decorators: [withDarkRemote] }
export const PairingExpired: Story = { args: { mode: 'pairing-expired', pairingCandidate: pairingCandidateData } }
export const Populated: Story = {
  args: {
    mode: 'populated',
    hosts: remoteHostsData.map((host, index) => index === 0 && host.currentJob
      ? { ...host, currentJob: { ...host.currentJob, pendingApprovals: 1 } }
      : host),
    announcement: 'Dragos MacBook Pro has one pending approval. Home Mac mini is offline. One Job is waiting for this Host.',
  },
}
export const PartialError: Story = {
  args: {
    mode: 'partial-error',
    hosts: remoteHostsData.map((host) => ({ ...host, currentJob: null })),
    partialErrorMessage: 'Hosts loaded, but current work and approval status could not be refreshed.',
    lastSuccessfulStatusAt: '2026-08-14T12:40:00Z',
  },
}