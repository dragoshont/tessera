import type { Meta, StoryObj } from '@storybook/react-vite'
import { pendingWrites } from '../../data/fixtures'
import { PendingWritesTable } from './PendingWritesTable'

const meta = {
  title: 'Awareness/PendingWritesTable',
  component: PendingWritesTable,
  parameters: { layout: 'padded' },
} satisfies Meta<typeof PendingWritesTable>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = { args: { items: pendingWrites } }

export const Loading: Story = { args: { isLoading: true } }

export const Empty: Story = {
  args: { items: [], emptyHint: 'No writes are waiting for your approval.' },
}

/** One row mid-decision: its buttons are busy + disabled while the mutation is in flight. */
export const Deciding: Story = {
  args: { items: pendingWrites, decidingId: pendingWrites[0]?.id },
}

/** A non-blocking error after a failed decision (e.g. the write expired) — shown, then refetched. */
export const WithError: Story = {
  args: {
    items: pendingWrites.slice(1),
    errorMessage:
      'That write is no longer waiting — it may have expired or already been decided. The list was refreshed.',
  },
}
