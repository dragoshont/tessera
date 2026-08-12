import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { useState } from 'react'
import userEvent from '@testing-library/user-event'
import axe from 'axe-core'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import type { FollowUpDetail, FollowUpSummary } from '../../data/types'
import { FollowUpWorkspace } from './FollowUpWorkspace'

const summary: FollowUpSummary = {
  followUpId: 'followup-1',
  status: 'attention',
  version: 2,
  deliverable: 'lease renewal checklist',
  counterparty: 'Rowan',
  dueAt: '2026-08-14',
  candidateCount: 1,
  conflictCount: 0,
  updatedAt: '2026-08-10T10:00:00Z',
}

const detail: FollowUpDetail = {
  followUpId: summary.followUpId,
  status: summary.status,
  version: summary.version,
  createdAt: '2026-08-10T09:00:00Z',
  updatedAt: summary.updatedAt,
  timelineTruncated: false,
  revisions: [
    {
      revisionId: 'revision-current',
      field: 'deliverable',
      value: 'lease renewal checklist',
      state: 'current',
      evidenceRefs: ['evidence-correction'],
      sourceTimestamp: '2026-08-10T09:00:00Z',
      parserVersion: '1',
      confidence: 1,
      correctionEvidenceRef: 'evidence-correction',
      lineageRevisionRefs: ['revision-original'],
      createdAt: '2026-08-10T10:00:00Z',
    },
    {
      revisionId: 'revision-candidate',
      field: 'dueAt',
      value: '2026-08-17',
      state: 'candidate',
      evidenceRefs: ['evidence-monday'],
      sourceTimestamp: '2026-08-11T09:00:00Z',
      parserVersion: 'followup.fixture.v1',
      confidence: 0.95,
      correctionEvidenceRef: null,
      lineageRevisionRefs: ['revision-current'],
      createdAt: '2026-08-11T09:01:00Z',
    },
  ],
  timeline: [
    {
      sequence: 1,
      kind: 'Imported',
      field: null,
      summary: 'Imported deterministic source evidence as candidate state.',
      evidenceRef: 'evidence-monday',
      sourceTimestamp: '2026-08-11T09:00:00Z',
      recordedAt: '2026-08-11T09:01:00Z',
    },
  ],
}

describe('FollowUpWorkspace', () => {
  it('renders candidate meaning with text and icon-backed state metadata', () => {
    render(<FollowUpWorkspace view="attention" items={[summary]} />)

    const badges = screen.getAllByText('Candidate')
    expect(badges.length).toBeGreaterThan(0)
    expect(badges[0].closest('[data-continuity-state]')).toHaveAttribute(
      'data-continuity-state',
      'attention',
    )
    expect(screen.getByRole('table')).toBeInTheDocument()
    expect(screen.getByRole('list', { name: 'Follow-ups' })).toBeInTheDocument()
  })

  it('exposes Detail, Timeline, and Why as labeled tabs', () => {
    render(<FollowUpWorkspace view="attention" items={[summary]} selected={detail} />)

    expect(screen.getByRole('tab', { name: 'Detail' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Timeline' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Why' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Accept candidate' })).toBeInTheDocument()
  })

  it('submits an explicit correction and disables submission while busy', async () => {
    const user = userEvent.setup()
    const onCorrect = vi.fn()
    const { rerender } = render(
      <FollowUpWorkspace view="attention" items={[summary]} selected={detail} onCorrect={onCorrect} />,
    )

    const correctButton = screen.getByRole('button', { name: 'Correct' })
    await user.click(correctButton)
    const input = screen.getByLabelText('New value')
    expect(screen.getByLabelText('Field')).toHaveFocus()
    await user.tab()
    expect(input).toHaveFocus()
    await user.clear(input)
    await user.type(input, 'renewal packet')

    rerender(
      <FollowUpWorkspace view="attention" items={[summary]} selected={detail} busy onCorrect={onCorrect} />,
    )
    expect(screen.getByRole('button', { name: 'Save correction' })).toBeDisabled()
    expect(document.querySelector('.motion-reduce\\:animate-none')).toBeInTheDocument()

    rerender(
      <FollowUpWorkspace view="attention" items={[summary]} selected={detail} onCorrect={onCorrect} />,
    )
    await user.click(screen.getByRole('button', { name: 'Save correction' }))
    expect(onCorrect).toHaveBeenCalledWith('deliverable', 'renewal packet')
    expect(screen.queryByRole('dialog', { name: 'Correct follow-up' })).not.toBeInTheDocument()
    expect(correctButton).toHaveFocus()
  })

  it('renders loading, empty, and error states without invented capability', () => {
    const { rerender } = render(<FollowUpWorkspace view="attention" isLoading />)
    expect(screen.getByLabelText('Loading follow-ups')).toBeInTheDocument()

    rerender(<FollowUpWorkspace view="attention" items={[]} />)
    expect(screen.getByText('Nothing needs your attention.')).toBeInTheDocument()

    rerender(<FollowUpWorkspace view="attention" items={[]} errorMessage="Continuity is unavailable." />)
    expect(screen.getByRole('alert')).toHaveTextContent('Continuity is unavailable.')
    expect(screen.queryByText(/chat|send externally/i)).not.toBeInTheDocument()
  })

  it('has no serious or critical axe violations in the populated detail state', async () => {
    render(<FollowUpWorkspace view="attention" items={[summary]} selected={detail} />)

    const result = await axe.run(document.body, {
      rules: { 'color-contrast': { enabled: false } },
    })
    expect(result.violations.filter((violation) =>
      violation.impact === 'serious' || violation.impact === 'critical')).toEqual([])
  })

  it.each(['attention', 'tracked', 'conflict', 'completed'] as const)(
    'has no serious or critical axe violations in the %s state',
    async (status) => {
      render(
        <FollowUpWorkspace
          view={status === 'tracked' || status === 'completed' ? 'tracked' : 'attention'}
          items={[{ ...summary, status }]}
          selected={{ ...detail, status }}
        />,
      )
      const result = await axe.run(document.body, { rules: { 'color-contrast': { enabled: false } } })
      expect(result.violations.filter((violation) =>
        violation.impact === 'serious' || violation.impact === 'critical')).toEqual([])
    },
  )

  it('closes correction with Escape and restores focus to Correct', async () => {
    const user = userEvent.setup()
    render(<FollowUpWorkspace view="attention" items={[summary]} selected={detail} />)
    const correct = screen.getByRole('button', { name: 'Correct' })

    await user.click(correct)
    expect(screen.getByRole('dialog', { name: 'Correct follow-up' })).toBeInTheDocument()
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('dialog', { name: 'Correct follow-up' })).not.toBeInTheDocument()
    expect(correct).toHaveFocus()
  })

  it('does not substitute detail revisions when Why fails', async () => {
    const user = userEvent.setup()
    render(
      <FollowUpWorkspace
        view="attention"
        items={[summary]}
        selected={detail}
        whyError
      />,
    )

    await user.click(screen.getByRole('tab', { name: 'Why' }))
    expect(screen.getByRole('alert')).toHaveTextContent('Source lineage is unavailable')
    expect(screen.queryByText('Parser')).not.toBeInTheDocument()
    expect(screen.queryByText('Evidence')).not.toBeInTheDocument()
  })

  it('closes detail with Escape and restores focus to its opener', async () => {
    const user = userEvent.setup()
    function Harness() {
      const [selected, setSelected] = useState<FollowUpDetail | null>(null)
      return (
        <FollowUpWorkspace
          view="attention"
          items={[summary]}
          selected={selected}
          onSelect={() => setSelected(detail)}
          onCloseDetail={() => setSelected(null)}
        />
      )
    }
    render(<Harness />)
    const opener = screen.getByRole('button', { name: 'Open lease renewal checklist' })

    await user.click(opener)
    expect(screen.getByRole('dialog', { name: /lease renewal checklist/i })).toBeInTheDocument()
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('dialog', { name: /lease renewal checklist/i })).not.toBeInTheDocument()
    expect(opener).toHaveFocus()
  })

  it('shows timeline and Why truncation without hiding the limits', async () => {
    const user = userEvent.setup()
    const why = {
      followUpId: detail.followUpId,
      fields: { deliverable: [detail.revisions[0]] },
      truncated: true,
    } as const
    render(
      <FollowUpWorkspace
        view="attention"
        items={[summary]}
        selected={{ ...detail, timelineTruncated: true }}
        why={why}
      />,
    )

    await user.click(screen.getByRole('tab', { name: 'Timeline' }))
    expect(screen.getByText('Showing the latest 100 timeline entries.')).toBeInTheDocument()
    await user.click(screen.getByRole('tab', { name: 'Why' }))
    expect(screen.getByText('Showing the latest 100 provenance revisions.')).toBeInTheDocument()
  })

  it('honors reduced-motion classes on the detail sheet and correction dialog', async () => {
    const user = userEvent.setup()
    render(<FollowUpWorkspace view="attention" items={[summary]} selected={detail} />)

    expect(screen.getByRole('dialog', { name: /lease renewal checklist/i })).toHaveClass('motion-reduce:transition-none')
    await user.click(screen.getByRole('button', { name: 'Correct' }))
    expect(screen.getByRole('dialog', { name: 'Correct follow-up' })).toHaveClass('motion-reduce:transition-none')
    expect(document.querySelectorAll('.motion-reduce\\:animate-none').length).toBeGreaterThan(0)
  })

  it('keeps continuity semantic text tokens at WCAG AA contrast in light and dark themes', () => {
    const css = readFileSync(resolve(process.cwd(), 'src/index.css'), 'utf8')
    const root = tokenBlock(css, ':root')
    const dark = tokenBlock(css, '.dark')

    for (const tokens of [root, dark]) {
      for (const foreground of ['--foreground', '--accent', '--health-live', '--health-expiring', '--health-error']) {
        expect(contrast(tokens[foreground], tokens['--card']), `${foreground} contrast`).toBeGreaterThanOrEqual(4.5)
      }
    }
  })

  it('provides responsive semantic variants, minimum targets, and keyboard tabs', async () => {
    const user = userEvent.setup()
    render(<FollowUpWorkspace view="attention" items={[summary]} />)

    expect(screen.getByRole('table').parentElement?.parentElement).toHaveClass('hidden', 'md:block')
    expect(screen.getByRole('list', { name: 'Follow-ups' })).toHaveClass('md:hidden')
    expect(screen.getAllByRole('button', { name: /Detail|Open detail/ })[0]).toHaveClass('h-8')
    expect(screen.getByRole('tab', { name: 'Attention' })).toHaveClass('py-1.5')

    const attention = screen.getByRole('tab', { name: 'Attention' })
    attention.focus()
    await user.keyboard('{ArrowRight}')
    expect(screen.getByRole('tab', { name: 'Tracked' })).toHaveFocus()
  })

  it('shows loading and truncation states explicitly', () => {
    render(
      <FollowUpWorkspace
        view="attention"
        items={[summary]}
        detailLoading
        listTruncated
      />,
    )
    expect(screen.getByText('Loading follow-up detail and provenance…')).toBeInTheDocument()
    expect(screen.getByText('Showing the latest 100 follow-ups in this view.')).toBeInTheDocument()
  })
})

function tokenBlock(css: string, selector: string): Record<string, string> {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  const body = new RegExp(`${escaped}\\s*\\{([^}]+)\\}`).exec(css)?.[1] ?? ''
  return Object.fromEntries([...body.matchAll(/(--[\w-]+):\s*(#[0-9a-fA-F]{6})/g)]
    .map((match) => [match[1], match[2]]))
}

function contrast(first: string, second: string): number {
  const luminance = (hex: string) => {
    const channels = [1, 3, 5].map((offset) => Number.parseInt(hex.slice(offset, offset + 2), 16) / 255)
      .map((channel) => channel <= 0.03928 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4)
    return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2]
  }
  const values = [luminance(first), luminance(second)].sort((left, right) => right - left)
  return (values[0] + 0.05) / (values[1] + 0.05)
}