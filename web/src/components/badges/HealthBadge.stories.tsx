import type { Meta, StoryObj } from '@storybook/react-vite'
import type { ConnectionStatus } from '../../data/types'
import { HealthBadge } from './HealthBadge'

const meta = {
  title: 'Badges/HealthBadge',
  component: HealthBadge,
  parameters: { layout: 'centered' },
} satisfies Meta<typeof HealthBadge>

export default meta
type Story = StoryObj<typeof meta>

export const Live: Story = { args: { status: 'live' } }
export const ExpiringSoon: Story = { args: { status: 'expiring_soon' } }
export const Absent: Story = { args: { status: 'absent' } }
export const Error: Story = { args: { status: 'error' } }
export const Seeding: Story = { args: { status: 'seeding' } }
export const NeedsHuman: Story = { args: { status: 'needs_human' } }

const ALL_STATES: ConnectionStatus[] = [
  'live',
  'expiring_soon',
  'absent',
  'error',
  'seeding',
  'needs_human',
]

export const AllStates: Story = {
  args: { status: 'live' },
  render: () => (
    <div className="flex flex-col gap-3">
      {ALL_STATES.map((status) => (
        <HealthBadge key={status} status={status} />
      ))}
    </div>
  ),
}
