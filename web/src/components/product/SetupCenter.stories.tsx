import type { Meta, StoryObj } from '@storybook/react-vite'
import { SetupCenter } from './SetupCenter'

const status = {
  server: { state: 'CONNECTED', displayName: 'Tessera Home', version: '2.0.0' },
  ai: { state: 'READY_TO_CONNECT', gatewayId: 'home', displayName: 'Home AI', model: 'example-model', profileId: null, detailCode: null },
  integrations: [
    { id: 'mail', name: 'Mail', state: 'READY_TO_CONNECT', runtimeState: 'READY', accountId: null, accountHealth: null, detailCode: 'account_authorization_required', connectPath: '/accounts' },
    { id: 'calendar', name: 'Calendar', state: 'CONNECTED', runtimeState: 'READY', accountId: 'account-1', accountHealth: 'Healthy', detailCode: null, connectPath: null },
  ],
  canOpenChat: false,
  requiredActionCount: 2,
}

const meta = { title: 'Product/SetupCenter', component: SetupCenter, parameters: { layout: 'padded', a11y: { test: 'error' } }, args: { status, busy: false, error: null, onRetry: () => undefined, onAccounts: () => undefined } } satisfies Meta<typeof SetupCenter>
export default meta
type Story = StoryObj<typeof meta>

export const ReadyToConnect: Story = {}
export const Connecting: Story = { args: { busy: true } }
export const Error: Story = { args: { error: new globalThis.Error('provider_unavailable') } }