import type { Meta, StoryObj } from '@storybook/react-vite'
import { demoLiveViewHandle } from '../../data/fixtures'
import { LiveHandoffStage } from './LiveHandoffStage'

// One story = one reachable state of the status machine (spec C.2 / D), wired to
// fixture props — no live backend. The hard-to-reach states (Expired, Error,
// Reconnecting, Unavailable) are first-class here, not afterthoughts.
const noop = () => undefined

const meta = {
  title: 'LiveHandoff',
  component: LiveHandoffStage,
  parameters: { layout: 'fullscreen' },
  args: {
    provider: 'Health Portal',
    ownerPrincipal: 'alice@example.com',
    hostname: demoLiveViewHandle.targetHostname,
    onStart: noop,
    onCancel: noop,
    onManualDone: noop,
    onRestart: noop,
    onPopOut: noop,
    onBackToAccounts: noop,
    onGetHelp: noop,
  },
  decorators: [
    (Story) => (
      <div className="mx-auto max-w-4xl p-4">
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof LiveHandoffStage>

export default meta
type Story = StoryObj<typeof meta>

export const Preflight: Story = { args: { status: 'preflight', handle: null } }

export const Connecting: Story = { args: { status: 'connecting', handle: null } }

export const WaitingForYou: Story = {
  args: { status: 'waiting', handle: demoLiveViewHandle },
}

export const Verifying: Story = {
  args: { status: 'verifying', handle: demoLiveViewHandle },
}

export const Done: Story = { args: { status: 'done', handle: demoLiveViewHandle } }

export const Reconnecting: Story = {
  args: { status: 'reconnecting', handle: demoLiveViewHandle },
}

export const Expired: Story = { args: { status: 'expired', handle: null } }

export const Paused: Story = { args: { status: 'paused', handle: null } }

// The honest fail-closed default: a calm "not set up yet" explainer, not an error.
export const Unavailable: Story = {
  args: {
    status: 'unavailable',
    handle: null,
    unavailableReason: 'live hand-off is not configured: no browser worker is wired (fail-closed).',
  },
}

export const ErrorState: Story = {
  name: 'Error',
  args: {
    status: 'error',
    handle: null,
    errorReason: "We couldn't verify the session. Try again.",
  },
}
