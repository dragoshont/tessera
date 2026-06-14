import type { Meta, StoryObj } from '@storybook/react-vite'
import { demoLiveViewHandle } from '../../data/fixtures'
import { TargetIdentityStrip } from './TargetIdentityStrip'

// The anti-phishing anchor: lock + local ProviderIcon tile + VERIFIED hostname +
// countdown. We never render a remote/spoofable favicon (anti-pattern #4).
const noop = () => undefined

const meta = {
  title: 'LiveHandoff/TargetIdentityStrip',
  component: TargetIdentityStrip,
  parameters: { layout: 'fullscreen' },
  args: {
    provider: 'Health Portal',
    hostname: demoLiveViewHandle.targetHostname,
    handle: demoLiveViewHandle,
    status: 'waiting',
    onExpire: noop,
    onPopOut: noop,
  },
  decorators: [
    (Story) => (
      <div className="mx-auto max-w-3xl p-4">
        <div className="overflow-hidden rounded-xl border border-border">
          <Story />
        </div>
      </div>
    ),
  ],
} satisfies Meta<typeof TargetIdentityStrip>

export default meta
type Story = StoryObj<typeof meta>

export const Verified: Story = {}

// Before a handle exists (Connecting): the strip stays honest — no verified host.
export const Preparing: Story = {
  args: { hostname: undefined, handle: null, status: 'connecting' },
}
