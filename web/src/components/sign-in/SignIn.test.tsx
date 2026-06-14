import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { portalConfigDev, portalConfigNone, portalConfigOidc } from '../../data/fixtures'
import { SignIn } from './SignIn'

describe('SignIn — dev loopback', () => {
  it('submits a principal and never exposes a password field', async () => {
    const user = userEvent.setup()
    const onDevSignIn = vi.fn()
    render(<SignIn config={portalConfigDev} onDevSignIn={onDevSignIn} />)

    expect(screen.getByText('Developer sign-in (local only)')).toBeInTheDocument()
    expect(screen.getByText(/loopback dev mode/i)).toBeInTheDocument()
    // Microsoft stays visible but disabled in dev mode.
    expect(screen.getByRole('button', { name: /sign in with microsoft/i })).toBeDisabled()
    expect(document.querySelectorAll('input[type="password"]')).toHaveLength(0)

    await user.type(screen.getByLabelText('Developer sign-in (local only)'), 'alice@example.com')
    await user.click(screen.getByRole('button', { name: /continue/i }))

    expect(onDevSignIn).toHaveBeenCalledWith('alice@example.com')
  })

  it('quick-pick fills the principal field', async () => {
    const user = userEvent.setup()
    render(<SignIn config={portalConfigDev} onDevSignIn={vi.fn()} />)

    await user.click(screen.getByRole('button', { name: 'bob@example.com' }))
    expect(screen.getByLabelText('Developer sign-in (local only)')).toHaveValue('bob@example.com')
  })
})

describe('SignIn — oidc', () => {
  it('offers an enabled Microsoft button that starts the redirect', async () => {
    const user = userEvent.setup()
    const onMicrosoftSignIn = vi.fn()
    render(<SignIn config={portalConfigOidc} onMicrosoftSignIn={onMicrosoftSignIn} />)

    const button = screen.getByRole('button', { name: /sign in with microsoft/i })
    expect(button).toBeEnabled()
    await user.click(button)
    expect(onMicrosoftSignIn).toHaveBeenCalledTimes(1)
  })
})

describe('SignIn — none', () => {
  it('explains that portal auth is not configured yet', () => {
    render(<SignIn config={portalConfigNone} />)
    expect(screen.getByText(/portal auth isn't configured yet/i)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /continue/i })).not.toBeInTheDocument()
  })
})
