import type { Meta, StoryObj } from '@storybook/react-vite'
import { aliceConnections, allExpiringConnections, liveConnection } from '../../data/fixtures'
import { AccountsTable } from './AccountsTable'

const meta = {
  title: 'Accounts/AccountsTable',
  component: AccountsTable,
  args: { ownerPrincipal: 'alice@example.com' },
} satisfies Meta<typeof AccountsTable>

export default meta
type Story = StoryObj<typeof meta>

export const MixedHealth: Story = { args: { connections: aliceConnections } }
export const SingleLive: Story = {
  args: { connections: [liveConnection], ownerPrincipal: liveConnection.ownerPrincipal },
}
export const Empty: Story = { args: { connections: [] } }
export const AllExpiring: Story = { args: { connections: allExpiringConnections } }
export const Loading: Story = { args: { connections: [], isLoading: true } }
export const ErrorState: Story = {
  name: 'Error',
  args: { connections: [], hasError: true },
}
