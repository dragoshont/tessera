import type { Meta, StoryObj } from '@storybook/react-vite'
import { aliceConnections } from '../../data/fixtures'
import { ConnectionDrawer } from './ConnectionDrawer'

const live = aliceConnections.find((connection) => connection.status === 'live')!
const absent = aliceConnections.find((connection) => connection.status === 'absent')!
const errored = aliceConnections.find((connection) => connection.status === 'error')!

const meta = {
  title: 'Accounts/ConnectionDrawer',
  component: ConnectionDrawer,
  parameters: { layout: 'fullscreen' },
  args: { open: true, onOpenChange: () => undefined },
} satisfies Meta<typeof ConnectionDrawer>

export default meta
type Story = StoryObj<typeof meta>

// Presence flags + the verbatim never-reveal line are visible; there is no
// reveal/copy affordance anywhere in the drawer.
export const Live: Story = { args: { connection: live } }
export const Absent: Story = { args: { connection: absent } }
export const ErrorState: Story = { name: 'Error', args: { connection: errored } }
