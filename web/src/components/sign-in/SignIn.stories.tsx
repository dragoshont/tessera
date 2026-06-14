import type { Meta, StoryObj } from '@storybook/react-vite'
import { portalConfigDev, portalConfigNone, portalConfigOidc } from '../../data/fixtures'
import { SignIn } from './SignIn'

const meta = {
  title: 'SignIn',
  component: SignIn,
  parameters: { layout: 'fullscreen' },
} satisfies Meta<typeof SignIn>

export default meta
type Story = StoryObj<typeof meta>

export const Microsoft: Story = { args: { config: portalConfigOidc } }
export const DeveloperLoopback: Story = { args: { config: portalConfigDev } }
export const NotConfigured: Story = { args: { config: portalConfigNone } }
export const Loading: Story = { args: { config: null } }
export const NotAllowed: Story = { args: { config: portalConfigOidc, error: 'not-allowed' } }
export const OidcFailed: Story = { args: { config: portalConfigOidc, error: 'oidc-failed' } }
export const SignedOut: Story = { args: { config: portalConfigOidc, signedOut: true } }
