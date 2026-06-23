import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { ConnectionDrawer } from './ConnectionDrawer'
import { aliceConnections, liveConnection } from '../../data/fixtures'

// A connection whose bundle is *present* (has fields). The never-reveal invariant is
// about showing presence without the value; status is irrelevant here (ADR 0025 made a
// present-but-unconfirmed session "unverified", not "live").
const presentConnection = aliceConnections.find((connection) => connection.hasRefreshToken)!

describe('ConnectionDrawer — never-reveal invariant', () => {
  it('shows bundle-field presence and the verbatim never-reveal line', () => {
    render(<ConnectionDrawer connection={presentConnection} open onOpenChange={() => undefined} />)

    expect(screen.getByText("Tessera can't show this — that's the point.")).toBeInTheDocument()
    expect(screen.getByText(/has cookies/i)).toBeInTheDocument()
    expect(screen.getByText(/has refresh token/i)).toBeInTheDocument()
  })

  it('exposes no reveal/copy affordance for any secret value', () => {
    render(<ConnectionDrawer connection={presentConnection} open onOpenChange={() => undefined} />)

    const controls = [...screen.queryAllByRole('button'), ...screen.queryAllByRole('menuitem')]
    for (const control of controls) {
      const label = (control.getAttribute('aria-label') ?? control.textContent ?? '').toLowerCase()
      expect(label).not.toMatch(/reveal|show secret|show value|copy|unmask|view value/)
    }

    // No secret-input fields exist anywhere in the drawer.
    expect(document.querySelectorAll('input[type="password"]')).toHaveLength(0)
  })

  it('is honest for an unverified connection — no false "· verified", surfaces "confirmed alive: never"', () => {
    render(<ConnectionDrawer connection={presentConnection} open onOpenChange={() => undefined} />)

    // ADR 0025: a use-timer is NOT a verification verdict — the header must not claim "· verified <time>".
    expect(screen.queryByText(/·\s*verified/i)).not.toBeInTheDocument()
    // The honest liveness provenance is surfaced instead.
    expect(screen.getByText('last confirmed alive')).toBeInTheDocument()
    expect(screen.getByText('never')).toBeInTheDocument()
  })

  it('shows "confirmed alive" for a verified-live connection — green earned by a verdict', () => {
    render(<ConnectionDrawer connection={liveConnection} open onOpenChange={() => undefined} />)

    // The header earns "· confirmed alive {time}" from lastVerifiedAt (a real verdict, not a use-timer)...
    expect(screen.getByText(/·\s*confirmed alive/i)).toBeInTheDocument()
    // ...and the Health section's provenance row reflects it (a real timestamp, not "never").
    expect(screen.getByText('last confirmed alive')).toBeInTheDocument()
  })
})
