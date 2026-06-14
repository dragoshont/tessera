import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { ConnectionDrawer } from './ConnectionDrawer'
import { aliceConnections } from '../../data/fixtures'

const liveConnection = aliceConnections.find((connection) => connection.status === 'live')!

describe('ConnectionDrawer — never-reveal invariant', () => {
  it('shows bundle-field presence and the verbatim never-reveal line', () => {
    render(<ConnectionDrawer connection={liveConnection} open onOpenChange={() => undefined} />)

    expect(screen.getByText("Tessera can't show this — that's the point.")).toBeInTheDocument()
    expect(screen.getByText(/has cookies/i)).toBeInTheDocument()
    expect(screen.getByText(/has refresh token/i)).toBeInTheDocument()
  })

  it('exposes no reveal/copy affordance for any secret value', () => {
    render(<ConnectionDrawer connection={liveConnection} open onOpenChange={() => undefined} />)

    const controls = [...screen.queryAllByRole('button'), ...screen.queryAllByRole('menuitem')]
    for (const control of controls) {
      const label = (control.getAttribute('aria-label') ?? control.textContent ?? '').toLowerCase()
      expect(label).not.toMatch(/reveal|show secret|show value|copy|unmask|view value/)
    }

    // No secret-input fields exist anywhere in the drawer.
    expect(document.querySelectorAll('input[type="password"]')).toHaveLength(0)
  })
})
