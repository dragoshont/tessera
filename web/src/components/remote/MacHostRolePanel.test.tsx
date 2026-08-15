import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { MacHostRolePanel } from './MacHostRolePanel'

const bundleIdentifier = 'ro.hont.tessera.host' as const

describe('MacHostRolePanel', () => {
  it('states client-only capability without exposing a false enable control', () => {
    render(<MacHostRolePanel status={{ available: false, state: 'CLIENT_ONLY', bundleIdentifier }} />)
    expect(screen.getByText(/Client only/)).toBeInTheDocument()
    expect(screen.queryByRole('button')).toBeNull()
  })

  it('emits only explicit enable intent', async () => {
    const onSetEnabled = vi.fn()
    const user = userEvent.setup()
    render(<MacHostRolePanel status={{ available: true, state: 'NOT_FOUND', bundleIdentifier }} onSetEnabled={onSetEnabled} />)
    expect(screen.getByText(/Available, not enabled/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Enable Host mode' }))
    expect(onSetEnabled).toHaveBeenCalledWith(true)
  })

  it('keeps approval-required state disabled with a reason', () => {
    render(<MacHostRolePanel status={{ available: true, state: 'REQUIRES_APPROVAL', bundleIdentifier }} />)
    expect(screen.getByText(/Approval required in System Settings/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Enable Host mode' })).toBeDisabled()
  })
})