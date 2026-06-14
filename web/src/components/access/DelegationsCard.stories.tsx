import type { Meta, StoryObj } from '@storybook/react-vite'
import { delegations } from '../../data/fixtures'
import { DelegationsCard } from './DelegationsCard'

const meta = {
  title: 'Awareness/DelegationsCard',
  component: DelegationsCard,
  parameters: { layout: 'padded' },
} satisfies Meta<typeof DelegationsCard>

export default meta
type Story = StoryObj<typeof meta>

/** A member's own view: only the grants that delegate to alice. */
export const Mine: Story = {
  args: { delegations: delegations.filter((d) => d.onBehalfOf === 'alice@example.com') },
}

/** The operator's all-view: every grant, including pure automation. */
export const Everyone: Story = { args: { delegations } }

export const Empty: Story = {
  args: { delegations: [], emptyHint: 'Nothing can act on your behalf.' },
}

export const Loading: Story = { args: { isLoading: true } }
