import axe from 'axe-core'
import { useState } from 'react'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import {
  RemoteHostDetail,
  RemoteWorkspace,
  type RemoteArtifact,
  type RemoteHostSummary,
  type RemotePairingCandidate,
} from './RemoteWorkspace'

const host: RemoteHostSummary = {
  hostId: 'host-main', version: 3, href: '/remote/hosts/host-main', displayName: 'Main Mac',
  platform: 'macOS 15.6', architecture: 'arm64', lifecycle: 'BUSY', agentVersion: '1.0.0',
  protocolVersion: '1', statusObservedAt: '2026-08-14T12:52:00Z', lastSeenAt: '2026-08-14T12:52:00Z',
  capabilityCount: 1, resourceCount: 1,
  currentJob: { runId: 'run-1', name: 'Inspect repository', state: 'RUNNING', href: '/jobs/job-1/runs/run-1' },
}

const candidate: RemotePairingCandidate = {
  pairingId: 'pairing-1', expiresAt: '2026-08-14T13:05:00Z', hostName: 'Main Mac', platform: 'macOS 15.6',
  architecture: 'arm64', protection: 'SECURE_ENCLAVE', agentVersion: '1.0.0', protocolVersion: '1',
  capabilities: [{ id: 'host.repo.identity@1', label: 'Repository identity', detail: 'Read only' }],
  resources: [{ id: 'repo-main', label: 'Tessera repository', detail: 'Opaque resource' }],
}

const artifact: RemoteArtifact = {
  artifactId: 'artifact-1', summary: 'Repository identity', kind: 'TEXT', mediaType: 'text/plain', sizeBytes: 48,
  sha256: 'a'.repeat(64), retention: 'RUN', createdAt: '2026-08-14T12:52:00Z', redacted: true, truncated: false,
  contentState: 'AVAILABLE', textContent: '<img src=x onerror=alert(1)>\nbranch=main',
}

describe('RemoteWorkspace', () => {
  it('renders honest loading and zero-Host states', () => {
    const { rerender } = render(<RemoteWorkspace mode="loading" />)
    expect(screen.getByLabelText('Checking Remote Host availability')).toHaveAttribute('aria-busy', 'true')
    rerender(<RemoteWorkspace mode="zero-hosts" />)
    expect(screen.getByRole('heading', { name: 'No Macs are paired' })).toBeInTheDocument()
    expect(screen.getAllByText(/Server Jobs continue normally/)).toHaveLength(2)
  })

  it('disables every zero-Host pairing control with the preview reason', () => {
    render(<RemoteWorkspace mode="zero-hosts" pairingUnavailableReason="Pairing awaits signed helper proof." />)
    for (const button of screen.getAllByRole('button', { name: 'Pair a Mac' })) {
      expect(button).toBeDisabled()
      expect(document.getElementById(button.getAttribute('aria-describedby')!)).toHaveTextContent('signed helper proof')
    }
  })

  it('requires all six pairing digits before continuing', async () => {
    const onContinue = vi.fn()
    const user = userEvent.setup()
    render(<RemoteWorkspace mode="pairing-code" pairingCandidate={candidate} onContinuePairing={onContinue} />)
    const continueButton = screen.getByRole('button', { name: 'Continue' })
    expect(continueButton).toBeDisabled()
    await user.type(screen.getByLabelText('Six-digit pairing code'), '12a3456')
    expect(continueButton).toBeEnabled()
    await user.click(continueButton)
    expect(onContinue).toHaveBeenCalledWith('123456')
  })

  it('restores focus to the Pair a Mac trigger after dismissing pairing', async () => {
    function PairingHarness() {
      const [mode, setMode] = useState<'zero-hosts' | 'pairing-code'>('zero-hosts')
      return (
        <RemoteWorkspace
          mode={mode}
          pairingCandidate={candidate}
          onPair={() => setMode('pairing-code')}
          onDismissPairing={() => setMode('zero-hosts')}
        />
      )
    }
    const user = userEvent.setup()
    render(<PairingHarness />)
    const trigger = screen.getAllByRole('button', { name: 'Pair a Mac' })[0]
    await user.click(trigger)
    await user.click(screen.getByRole('button', { name: 'Close' }))
    await waitFor(() => expect(trigger).toHaveFocus())
  })

  it('associates the unavailable pairing reason', () => {
    render(<RemoteWorkspace mode="unsupported" />)
    const button = screen.getByRole('button', { name: 'Pair a Mac' })
    expect(button).toBeDisabled()
    expect(document.getElementById(button.getAttribute('aria-describedby')!)).toHaveTextContent('server capability check')
  })

  it('keeps capability and resource grants independent during pairing review', async () => {
    const onConfirm = vi.fn()
    const user = userEvent.setup()
    render(<RemoteWorkspace mode="pairing-review" pairingCandidate={candidate} onConfirmPairing={onConfirm} />)
    await user.click(screen.getByRole('checkbox', { name: /Repository identity/ }))
    expect(screen.getByText(/cannot run Jobs until both capability and resource access are granted/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Pair Mac' }))
    expect(onConfirm).toHaveBeenCalledWith({ displayName: 'Main Mac', capabilityIds: [], resourceIds: ['repo-main'] })
  })

  it('supports both semantic inventory representations and passes axe', async () => {
    render(<RemoteWorkspace mode="populated" hosts={[host]} />)
    expect(screen.getByRole('table', { name: 'Paired Remote Hosts' })).toBeInTheDocument()
    const mobileInventory = screen.getByRole('list', { name: 'Paired Remote Hosts' })
    expect(mobileInventory).toHaveAttribute('data-layout', 'responsive-list')
    expect(screen.getAllByText('Agent 1.0.0 · protocol 1')).toHaveLength(1)
    expect(within(mobileInventory).getByText('Agent')).toBeInTheDocument()
    expect(within(mobileInventory).getByText('1.0.0')).toBeInTheDocument()
    expect(within(mobileInventory).getByText('Protocol')).toBeInTheDocument()
    expect(within(mobileInventory).getByText('1', { selector: 'dd' })).toBeInTheDocument()
    expect(within(mobileInventory).getByText('Status observed')).toBeInTheDocument()
    expect(within(mobileInventory).getByText('Pending approvals')).toBeInTheDocument()
    const result = await axe.run(document.body, { rules: { 'color-contrast': { enabled: false } } })
    expect(result.violations.filter((item) => ['serious', 'critical'].includes(item.impact ?? ''))).toEqual([])
  })
})

describe('RemoteHostDetail', () => {
  it('focuses cancel first and requires an exact Host name before revoke', async () => {
    const onRevoke = vi.fn()
    const user = userEvent.setup()
    render(<RemoteHostDetail state="busy-running" host={host} onRevoke={onRevoke} />)
    const trigger = screen.getByRole('button', { name: 'Revoke Host…' })
    await user.click(trigger)
    expect(screen.getByRole('button', { name: 'Cancel' })).toHaveFocus()
    const destructive = screen.getByRole('button', { name: 'Revoke Host' })
    expect(destructive).toBeDisabled()
    await user.type(screen.getByLabelText('Type Main Mac to confirm'), 'Main Mac')
    expect(destructive).toBeEnabled()
    await user.click(destructive)
    expect(onRevoke).toHaveBeenCalledWith(host)
    await waitFor(() => expect(trigger).toHaveFocus())
  })

  it('keeps revoke bound to the displayed revision while polling updates the Host', async () => {
    const onRevoke = vi.fn()
    const user = userEvent.setup()
    const { rerender } = render(<RemoteHostDetail state="busy-running" host={host} onRevoke={onRevoke} />)
    await user.click(screen.getByRole('button', { name: 'Revoke Host…' }))
    expect(screen.getByText(/Host revision 3/)).toBeInTheDocument()
    rerender(<RemoteHostDetail state="busy-running" host={{ ...host, version: 4 }} onRevoke={onRevoke} />)
    await user.type(screen.getByLabelText('Type Main Mac to confirm'), 'Main Mac')
    await user.click(screen.getByRole('button', { name: 'Revoke Host' }))
    expect(onRevoke).toHaveBeenCalledWith(expect.objectContaining({ hostId: 'host-main', version: 3 }))
  })

  it('confirms Job cancellation and restores focus to its trigger', async () => {
    const onCancel = vi.fn()
    const user = userEvent.setup()
    render(<RemoteHostDetail state="busy-running" host={host} onCancel={onCancel} />)
    const trigger = screen.getByRole('button', { name: 'Cancel Job' })
    await user.click(trigger)
    const dialog = screen.getByRole('dialog', { name: /Cancel Inspect repository/ })
    expect(within(dialog).getByRole('button', { name: 'Keep Job running' })).toHaveFocus()
    await user.click(within(dialog).getByRole('button', { name: 'Cancel Job' }))
    expect(onCancel).toHaveBeenCalledOnce()
    await waitFor(() => expect(trigger).toHaveFocus())
  })

  it('renders artifact content only as escaped plain text', async () => {
    const user = userEvent.setup()
    render(<RemoteHostDetail state="succeeded-with-artifacts" host={{ ...host, lifecycle: 'ONLINE' }} artifacts={[artifact]} />)
    await user.click(screen.getByRole('button', { name: 'Preview' }))
    const dialog = screen.getByRole('dialog')
    expect(within(dialog).getByText('Repository identity')).toBeInTheDocument()
    expect(dialog.querySelector('pre')).toHaveTextContent('<img src=x onerror=alert(1)> branch=main')
    expect(dialog.querySelector('img')).toBeNull()
  })

  it('loads artifact text only after explicit preview intent', async () => {
    const user = userEvent.setup()
    const onLoadArtifact = vi.fn(async () => ({ ...artifact, textContent: 'loaded plain text' }))
    render(<RemoteHostDetail state="succeeded-with-artifacts" host={{ ...host, lifecycle: 'ONLINE' }} artifacts={[{ ...artifact, textContent: undefined }]} onLoadArtifact={onLoadArtifact} />)

    expect(onLoadArtifact).not.toHaveBeenCalled()
    await user.click(screen.getByRole('button', { name: 'Preview' }))

    expect(onLoadArtifact).toHaveBeenCalledWith(artifact.artifactId)
    expect(await screen.findByText('loaded plain text')).toBeInTheDocument()
  })

  it('keeps expired metadata and associates the disabled preview reason', () => {
    const expired = { ...artifact, contentState: 'EXPIRED' as const, textContent: null, expiresAt: '2026-08-15T12:52:00Z' }
    render(<RemoteHostDetail state="expired-artifact" host={{ ...host, lifecycle: 'ONLINE' }} artifacts={[expired]} />)
    const preview = screen.getByRole('button', { name: 'Preview' })
    expect(preview).toBeDisabled()
    const reasonId = preview.getAttribute('aria-describedby')
    expect(reasonId).toBeTruthy()
    expect(document.getElementById(reasonId!)).toHaveTextContent('metadata remains available')
  })

  it('exposes disabled pause reasons and passes axe', async () => {
    render(<RemoteHostDetail state="offline-waiting-for-host" host={{ ...host, lifecycle: 'OFFLINE' }} blocker="Waiting for Main Mac to reconnect." />)
    const pause = screen.getByRole('button', { name: 'Pause after current step' })
    expect(pause).toBeDisabled()
    expect(document.getElementById(pause.getAttribute('aria-describedby')!)).toHaveTextContent('No Host step is running')
    fireEvent.keyDown(document, { key: 'Escape' })
    const result = await axe.run(document.body, { rules: { 'color-contrast': { enabled: false } } })
    expect(result.violations.filter((item) => ['serious', 'critical'].includes(item.impact ?? ''))).toEqual([])
  })
})