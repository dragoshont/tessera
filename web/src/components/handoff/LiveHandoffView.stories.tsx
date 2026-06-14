import type { Meta, StoryObj } from '@storybook/react-vite'
import { createInMemoryClient } from '../../api/client'
import { demoLiveViewHandle } from '../../data/fixtures'
import { LiveHandoffView } from './LiveHandoffView'

// These stories drive the WHOLE machine through an in-memory client seed (no real
// backend): `liveView` mints the demo handle for the happy path, and the default
// client returns the fail-closed Unavailable. `autoStart` skips the pre-flight so
// the story lands on the resulting state for a screenshot.
const happyClient = createInMemoryClient({
  liveView: () => ({ handle: demoLiveViewHandle }),
})
const failClosedClient = createInMemoryClient() // default → { unavailable }

const meta = {
  title: 'LiveHandoff/Driver',
  component: LiveHandoffView,
  parameters: { layout: 'fullscreen' },
  args: {
    connectionId: 'health:alice@example.com',
    provider: 'Health Portal',
    ownerPrincipal: 'alice@example.com',
    autoStart: true,
    onExit: () => undefined,
  },
  decorators: [
    (Story) => (
      <div className="mx-auto max-w-4xl p-4">
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof LiveHandoffView>

export default meta
type Story = StoryObj<typeof meta>

/** Seed mints a handle → Connecting → (iframe ready) → Waiting for you. */
export const HappyPath: Story = {
  args: { requestLiveView: happyClient.requestLiveView },
}

/** Default fail-closed seed → the calm Unavailable explainer (the honest default). */
export const Unavailable: Story = {
  args: { requestLiveView: failClosedClient.requestLiveView },
}
