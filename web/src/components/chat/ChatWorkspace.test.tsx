import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import axe from 'axe-core'
import { describe, expect, it, vi } from 'vitest'
import { ChatWorkspace } from './ChatWorkspace'

describe('ChatWorkspace realtime voice region', () => {
  it('starts only from an explicit user action and keeps typed Chat available', async () => {
    const user = userEvent.setup()
    const start = vi.fn()
    render(<ChatWorkspace voice={{ state: 'IDLE' }} onVoiceStart={start} />)
    await user.click(screen.getByRole('button', { name: 'Start voice' }))
    expect(start).toHaveBeenCalledOnce()
    expect(screen.getByPlaceholderText('Message Tessera')).toBeEnabled()
  })

  it('renders speaking captions, mute state, interrupt, and end controls accessibly', async () => {
    render(<ChatWorkspace voice={{ state: 'ASSISTANT_SPEAKING', userCaption: 'Hello', assistantCaption: 'Hi there' }} />)
    expect(screen.getByText('Tessera is speaking')).toBeInTheDocument()
    expect(screen.getByLabelText('Voice captions')).toHaveTextContent('Hello')
    expect(screen.getByRole('button', { name: 'Mute' })).toHaveAttribute('aria-pressed', 'false')
    expect(screen.getByRole('button', { name: 'Interrupt' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'End voice' })).toBeEnabled()
    const result = await axe.run(document.body, { rules: { 'color-contrast': { enabled: false } } })
    expect(result.violations.filter((item) => item.impact === 'critical' || item.impact === 'serious')).toEqual([])
  })

  it('never presents spoken approval as an authorization control', () => {
    render(<ChatWorkspace voice={{ state: 'APPROVAL_REQUIRED', toolName: 'GitHub issue create' }} />)
    expect(screen.getByText(/cannot approve consequential actions/i)).toBeInTheDocument()
    expect(screen.getByText('Review Action below')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /approve/i })).not.toBeInTheDocument()
  })

  it('keeps unavailable voice disabled and exposes a safe reason', () => {
    render(<ChatWorkspace voice={{ state: 'UNAVAILABLE', blockedCode: 'Deployment not configured.' }} />)
    expect(screen.getByRole('button', { name: 'Voice unavailable' })).toBeDisabled()
    expect(screen.getByText('Deployment not configured.')).toBeInTheDocument()
  })
})