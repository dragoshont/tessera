import type { Meta, StoryObj } from '@storybook/react-vite'
import { Countdown } from './Countdown'

// Anchor each story relative to "now" so the rendered value is deterministic.
const inSeconds = (seconds: number) => new Date(Date.now() + seconds * 1000).toISOString()

const meta = {
  title: 'LiveHandoff/Countdown',
  component: Countdown,
  parameters: { layout: 'centered' },
  args: { running: true },
} satisfies Meta<typeof Countdown>

export default meta
type Story = StoryObj<typeof meta>

export const Normal: Story = { args: { expiresAt: inSeconds(278) } } // ~4:38
export const Warning: Story = { args: { expiresAt: inSeconds(48) } } // < 1:00, amber
export const Expired: Story = { args: { expiresAt: inSeconds(0), running: false } } // 0:00
export const Frozen: Story = {
  name: 'Frozen (Verifying)',
  args: { expiresAt: inSeconds(150), running: false },
}
