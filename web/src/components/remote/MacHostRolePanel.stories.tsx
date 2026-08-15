import type { Meta, StoryObj } from '@storybook/react-vite'
import { MacHostRolePanel } from './MacHostRolePanel'

const meta = {
  title: 'Product/MacHostRolePanel',
  component: MacHostRolePanel,
  args: { onSetEnabled: () => undefined },
} satisfies Meta<typeof MacHostRolePanel>

export default meta
type Story = StoryObj<typeof meta>

const bundleIdentifier = 'ro.hont.tessera.host' as const

export const ClientOnly: Story = { args: { status: { available: false, state: 'CLIENT_ONLY', bundleIdentifier } } }
export const AvailableNotEnabled: Story = { args: { status: { available: true, state: 'NOT_FOUND', bundleIdentifier } } }
export const Enabled: Story = { args: { status: { available: true, state: 'ENABLED', bundleIdentifier } } }
export const ApprovalRequired: Story = { args: { status: { available: true, state: 'REQUIRES_APPROVAL', bundleIdentifier } } }
export const Unavailable: Story = { args: { status: { available: true, state: 'UNAVAILABLE', bundleIdentifier }, error: 'The packaged helper did not return a valid status.' } }