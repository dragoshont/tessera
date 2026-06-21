import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { PendingWritesTable } from './PendingWritesTable'
import { pendingWrites } from '../../data/fixtures'

describe('PendingWritesTable', () => {
  it('renders each held write with its summary, method, and Approve/Deny actions', () => {
    render(<PendingWritesTable items={pendingWrites} />)

    for (const write of pendingWrites) {
      expect(screen.getByText(write.summary)).toBeInTheDocument()
    }
    // Exactly one Approve + one Deny per held write.
    expect(screen.getAllByRole('button', { name: /^Approve:/ })).toHaveLength(pendingWrites.length)
    expect(screen.getAllByRole('button', { name: /^Deny:/ })).toHaveLength(pendingWrites.length)
    // The upstream resource (method) is shown so the human knows the exact change.
    expect(screen.getAllByText('PUT').length).toBe(pendingWrites.length)
  })

  it('invokes onApprove / onDeny with the write id', () => {
    const onApprove = vi.fn()
    const onDeny = vi.fn()
    const [first] = pendingWrites
    render(<PendingWritesTable items={pendingWrites} onApprove={onApprove} onDeny={onDeny} />)

    fireEvent.click(screen.getByLabelText(`Approve: ${first.summary}`))
    expect(onApprove).toHaveBeenCalledWith(first.id)

    fireEvent.click(screen.getByLabelText(`Deny: ${first.summary}`))
    expect(onDeny).toHaveBeenCalledWith(first.id)
  })

  it('disables only the row whose decision is in flight', () => {
    const [first, second] = pendingWrites
    render(<PendingWritesTable items={pendingWrites} decidingId={first.id} />)

    expect(screen.getByLabelText(`Approve: ${first.summary}`)).toBeDisabled()
    expect(screen.getByLabelText(`Deny: ${first.summary}`)).toBeDisabled()
    // Other rows stay actionable.
    expect(screen.getByLabelText(`Approve: ${second.summary}`)).not.toBeDisabled()
  })

  it('shows the empty hint when nothing is waiting', () => {
    render(<PendingWritesTable items={[]} emptyHint="No writes are waiting for your approval." />)

    expect(screen.getByText('No writes are waiting for your approval.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^Approve:/ })).not.toBeInTheDocument()
  })

  it('surfaces a non-blocking error message when a decision fails', () => {
    render(
      <PendingWritesTable items={pendingWrites} errorMessage="That write is no longer waiting." />,
    )

    expect(screen.getByRole('alert')).toHaveTextContent('That write is no longer waiting.')
  })
})
