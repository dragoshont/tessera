import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { ConnectionStatus } from '../../data/types'
import { HealthBadge } from './HealthBadge'

// ADR 0025 — presence is not liveness. The badge must never render a present-but-
// unconfirmed session as green "Live", and must never crash on an unknown status.
describe('HealthBadge — honest, fail-closed health', () => {
  it('renders "live" using the green token (the only green state)', () => {
    const { container } = render(<HealthBadge status="live" />)
    screen.getByText('Live') // throws if missing
    expect(container.querySelector('.text-health-live')).not.toBeNull()
  })

  it('renders a present-but-unverified session as amber "Unverified", never green', () => {
    const { container } = render(<HealthBadge status="unverified" />)
    screen.getByText('Unverified')
    expect(container.querySelector('.text-health-live')).toBeNull() // NOT green
    expect(container.querySelector('.text-health-expiring')).not.toBeNull() // amber caution
  })

  it('renders a verified-dead session as red "Dead" (distinct from a store error)', () => {
    const { container } = render(<HealthBadge status="dead" />)
    screen.getByText('Dead')
    expect(container.querySelector('.text-health-error')).not.toBeNull()
    expect(container.querySelector('.text-health-live')).toBeNull()
  })

  it('fails closed: an unrecognised status renders a neutral "Unknown" — no crash, not green', () => {
    // Simulate the backend sending a status the UI does not model yet — the total
    // VISUALS record would otherwise throw on the missing key.
    const { container } = render(<HealthBadge status={'totally-new-status' as ConnectionStatus} />)
    screen.getByText('Unknown')
    expect(container.querySelector('.text-health-live')).toBeNull()
  })
})
