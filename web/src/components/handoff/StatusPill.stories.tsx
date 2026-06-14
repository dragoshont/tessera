import type { Meta, StoryObj } from '@storybook/react-vite'
import { StatusPill } from './StatusPill'
import type { HandoffStatus } from './handoff-machine'

const meta = {
  title: 'LiveHandoff/StatusPill',
  component: StatusPill,
  parameters: { layout: 'centered' },
} satisfies Meta<typeof StatusPill>

export default meta
type Story = StoryObj<typeof meta>

export const Connecting: Story = { args: { status: 'connecting' } }
export const WaitingForYou: Story = { args: { status: 'waiting' } }
export const Verifying: Story = { args: { status: 'verifying' } }
export const Done: Story = { args: { status: 'done' } }
export const Reconnecting: Story = { args: { status: 'reconnecting' } }
export const Expired: Story = { args: { status: 'expired' } }
export const ErrorState: Story = { name: 'Error', args: { status: 'error' } }

const ALL: HandoffStatus[] = [
  'connecting',
  'waiting',
  'verifying',
  'done',
  'reconnecting',
  'expired',
  'paused',
  'unavailable',
  'error',
]

export const AllStates: Story = {
  args: { status: 'waiting' },
  render: () => (
    <div className="flex flex-col gap-3">
      {ALL.map((status) => (
        <StatusPill key={status} status={status} />
      ))}
    </div>
  ),
}
