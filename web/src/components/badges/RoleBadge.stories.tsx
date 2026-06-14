import type { Meta, StoryObj } from '@storybook/react-vite'
import { RoleBadge } from './RoleBadge'

const meta = {
  title: 'Badges/RoleBadge',
  component: RoleBadge,
  parameters: { layout: 'centered' },
} satisfies Meta<typeof RoleBadge>

export default meta
type Story = StoryObj<typeof meta>

export const Admin: Story = { args: { role: 'Admin' } }
export const Member: Story = { args: { role: 'Member' } }
