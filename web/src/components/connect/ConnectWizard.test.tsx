import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { Connection, CreateConnectionInput, LiveViewResult } from '../../data/types'
import { recipes } from '../../data/fixtures'
import { ConnectWizard, type ConnectWizardProps } from './ConnectWizard'

const FAIL_CLOSED: LiveViewResult = {
  unavailable: 'live hand-off is not configured: no browser worker is wired (fail-closed).',
}

const synthConnection = (input: CreateConnectionInput): Connection => ({
  connectionId: `${input.provider}:${input.principal}`,
  ownerPrincipal: input.principal,
  provider: input.provider,
  displayName: input.provider,
  status: 'absent',
  expiryIsEstimated: false,
  hasCookies: false,
  hasRefreshToken: false,
  hasAccessToken: false,
})

function renderWizard(overrides: Partial<ConnectWizardProps> = {}) {
  const props: ConnectWizardProps = {
    recipes,
    currentPrincipal: 'alice@example.com',
    isAdmin: true,
    requestSeed: vi.fn(async () => FAIL_CLOSED),
    createConnection: vi.fn(async (input) => synthConnection(input)),
    onCreated: vi.fn(),
    onCancel: vi.fn(),
    ...overrides,
  }
  render(<ConnectWizard {...props} />)
  return props
}

describe('ConnectWizard — never-reveal invariant', () => {
  it('the stored-credential field is a name, not a secret value', () => {
    renderWizard({
      initialState: {
        step: 'credential',
        draft: { provider: 'health', displayName: 'Health Portal', principal: 'bob@example.com' },
      },
    })

    const field = screen.getByLabelText('Stored credential name')
    expect(field).toHaveAttribute('type', 'text')
    expect(document.querySelectorAll('input[type="password"]')).toHaveLength(0)

    // The never-reveal line is present verbatim.
    expect(screen.getByText("Tessera can't show this — that's the point.")).toBeInTheDocument()

    // No reveal / copy affordance anywhere.
    for (const control of screen.queryAllByRole('button')) {
      const label = (control.getAttribute('aria-label') ?? control.textContent ?? '').toLowerCase()
      expect(label).not.toMatch(/reveal|show secret|show value|copy|unmask|view value/)
    }
  })
})

describe('ConnectWizard — connect flow', () => {
  it('walks provider → person → credential → seed → finish and POSTs the binding', async () => {
    const user = userEvent.setup()
    const props = renderWizard()

    await user.click(screen.getByRole('button', { name: 'Health Portal' }))
    await user.click(screen.getByRole('button', { name: /next/i }))

    // Person defaults to the signed-in operator.
    expect(screen.getByLabelText(/^Person/)).toHaveValue('alice@example.com')
    await user.click(screen.getByRole('button', { name: /next/i }))

    await user.type(screen.getByLabelText('Stored credential name'), 'health-alice-session')
    await user.click(screen.getByRole('button', { name: /next/i }))

    // Seed is the fail-closed normal case; skip and continue.
    await user.click(screen.getByRole('button', { name: /skip — i'll seed later/i }))

    await user.click(screen.getByRole('button', { name: /^connect$/i }))

    expect(props.createConnection).toHaveBeenCalledWith({
      provider: 'health',
      principal: 'alice@example.com',
      credential: 'health-alice-session',
    })
    expect(props.onCreated).toHaveBeenCalledTimes(1)
  })

  it('shows the calm "not configured yet" panel on a fail-closed seed (no error)', async () => {
    const user = userEvent.setup()
    renderWizard({
      requestSeed: vi.fn(async () => FAIL_CLOSED),
      initialState: {
        step: 'seed',
        draft: {
          provider: 'health',
          displayName: 'Health Portal',
          principal: 'alice@example.com',
          credential: 'health-alice-session',
        },
      },
    })

    await user.click(screen.getByRole('button', { name: /seed now/i }))

    expect(await screen.findByText(/live hand-off isn't configured yet/i)).toBeInTheDocument()
    expect(screen.getByText(/fail-closed/i)).toBeInTheDocument()
    // Still advanceable — seeding is optional right now.
    expect(screen.getByRole('button', { name: /skip — i'll seed later/i })).toBeEnabled()
  })

  it('locks a member to their own principal', () => {
    renderWizard({
      isAdmin: false,
      currentPrincipal: 'bob@example.com',
      initialState: {
        step: 'person',
        draft: { provider: 'health', displayName: 'Health Portal' },
      },
    })

    expect(screen.getByLabelText(/^Person/)).toBeDisabled()
    expect(screen.getByText(/members can only connect their own accounts/i)).toBeInTheDocument()
  })

  it('surfaces a 403 from the broker as an inline message, not a crash', async () => {
    const user = userEvent.setup()
    const { HttpError } = await import('../../api/client')
    const createConnection = vi.fn(async () => {
      throw new HttpError(403, 'forbidden')
    })
    renderWizard({
      createConnection,
      initialState: {
        step: 'finish',
        draft: {
          provider: 'health',
          displayName: 'Health Portal',
          principal: 'carol@example.com',
          credential: 'health-carol-session',
        },
      },
    })

    await user.click(screen.getByRole('button', { name: /^connect$/i }))

    expect(
      await screen.findByText(/don’t have permission to connect this account/i),
    ).toBeInTheDocument()
  })
})
