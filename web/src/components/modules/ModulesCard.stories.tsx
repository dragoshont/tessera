import type { Meta, StoryObj } from '@storybook/react-vite'
import { modules } from '../../data/fixtures'
import { ModulesCard } from './ModulesCard'

const meta = {
  title: 'Awareness/ModulesCard',
  component: ModulesCard,
  parameters: { layout: 'padded' },
} satisfies Meta<typeof ModulesCard>

export default meta
type Story = StoryObj<typeof meta>

/** A status-only module and an egress-enabled one side by side. */
export const Default: Story = { args: { modules } }

export const Empty: Story = { args: { modules: [] } }

export const Loading: Story = { args: { isLoading: true } }
