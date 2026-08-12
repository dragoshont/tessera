import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import axe from 'axe-core'
import { describe, expect, it, vi } from 'vitest'
import { ActionApprovalCard, JobRunTimeline } from './R2ProductComponents'
import type { R2Action, R2JobRunDetail } from '../../api/r2'

const action: R2Action = {
  id: 'action-1', conversationId: 'conversation-1', messageId: 'message-1', jobId: null, jobRunId: null,
  pluginId: 'github', pluginVersion: '1.0.0', capabilityId: 'github.issues.create', capabilityVersion: '1',
  accountId: 'account-1', target: 'owner/sandbox', payloadPreview: { title: 'Exact title', body: 'Exact body' },
  state: 'PROPOSED', expiresAt: '2026-08-10T18:00:00Z', providerReceipt: null,
  verificationState: null, failureCode: null, version: 0,
}

describe('R2 product execution components', () => {
  it('shows exact proposal identity and requires an explicit approval click', async () => {
    const user = userEvent.setup()
    const approve = vi.fn()
    const cancel = vi.fn()
    render(<ActionApprovalCard action={action} accountLabel="Work GitHub" onApprove={approve} onCancel={cancel} />)

    expect(screen.getByText('github.issues.create')).toBeInTheDocument()
    expect(screen.getByText('owner/sandbox')).toBeInTheDocument()
    expect(screen.getByText('Work GitHub')).toBeInTheDocument()
    expect(screen.getByText(/Exact title/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Approve exact action' }))
    expect(approve).toHaveBeenCalledOnce()
    expect(cancel).not.toHaveBeenCalled()
  })

  it('renders unknown outcome as reconciliation without approval controls', () => {
    render(<ActionApprovalCard action={{ ...action, state: 'RECONCILIATION_REQUIRED', failureCode: 'provider_timeout' }} />)

    expect(screen.getByText('Outcome unknown')).toBeInTheDocument()
    expect(screen.getByText(/will not retry/i)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /approve/i })).not.toBeInTheDocument()
  })

  it('shows human-readable exact Regina Maria booking details', () => {
    render(<ActionApprovalCard action={{...action,pluginId:'regina-maria',capabilityId:'reginamaria.appointment.book',accountId:'rm-owner',target:'appointment:book',payloadPreview:{intervalId:'slot-ref',physicianId:'doctor-ref',doctor:'Doctor One',specialty:'Cardiology',service:'Consultation',location:'Victoriei',date:'2026-08-20',time:'17:00',mode:'in-clinic',price:0,currency:'RON'}}} accountLabel="My Regina Maria" />)
    expect(screen.getByText('Confirms this exact appointment in Regina Maria.')).toBeInTheDocument()
    expect(screen.getByText('My Regina Maria')).toBeInTheDocument()
    expect(screen.getByText('Doctor One')).toBeInTheDocument()
    expect(screen.getByText('Victoriei')).toBeInTheDocument()
    expect(screen.getByText('0 RON')).toBeInTheDocument()
  })

  it('shows exact Gmail recipients, subject, and body before sending', () => {
    render(<ActionApprovalCard action={{...action,pluginId:'gmail',capabilityId:'gmail.messages.send',accountId:'gmail-owner',target:'mailbox:send',payloadPreview:{from:'owner@example.com',to:['recipient@example.com'],cc:[],bcc:[],subject:'Exact subject',body:'Exact message body'}}} accountLabel="My Gmail" />)
    expect(screen.getByText('Sends this exact message from the selected Gmail account.')).toBeInTheDocument()
    expect(screen.getByText('recipient@example.com')).toBeInTheDocument()
    expect(screen.getByText('Exact subject')).toBeInTheDocument()
    expect(screen.getByText('Exact message body')).toBeInTheDocument()
  })

  it('renders durable Job trace and pending Action accessibly', async () => {
    const detail: R2JobRunDetail = {
      run: { id: 'run-1', runId: 'run-1', jobId: 'job-1', scheduledFor: '2026-08-10T17:00:00Z', state: 'WAITING_FOR_APPROVAL', startedAt: '2026-08-10T17:00:01Z', endedAt: null, modelProfileId: null, contextSnapshotRef: null, capabilityCallIds: [], accountIds: [], actionIds: ['action-1'], outputRefs: [], evidenceRefs: [], errorCode: null, version: 2 },
      contextSnapshot: null, capabilityUses: { items: [], nextCursor: null }, accountUses: { items: [], nextCursor: null },
      actions: { items: [{ ...action, jobId: 'job-1', jobRunId: 'run-1' }], nextCursor: null }, outputs: { items: [], nextCursor: null }, evidence: { items: [], nextCursor: null },
      trace: { items: [{ sequence: 1, occurredAt: '2026-08-10T17:00:01Z', type: 'awaiting_user_approval', summary: 'Waiting for exact user approval', actionId: 'action-1', errorCode: null }], nextCursor: null },
    }
    render(<JobRunTimeline detail={detail} />)

    expect(screen.getByText('Waiting for approval')).toBeInTheDocument()
    expect(screen.getByText('Waiting for exact user approval')).toBeInTheDocument()
    const result = await axe.run(document.body, { rules: { 'color-contrast': { enabled: false } } })
    expect(result.violations.filter((item) => item.impact === 'critical' || item.impact === 'serious')).toEqual([])
  })
})
