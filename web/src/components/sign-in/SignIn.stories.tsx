import type { Meta, StoryObj } from '@storybook/react-vite'
import { SignIn } from './SignIn'

const meta = {
  title: 'SignIn',
  component: SignIn,
  parameters: { layout: 'fullscreen' },
} satisfies Meta<typeof SignIn>

export default meta
type Story = StoryObj<typeof meta>

export const Microsoft: Story = {}
export const NotAllowed: Story = { args: { error: 'not-allowed' } }
export const OidcFailed: Story = { args: { error: 'oidc-failed' } }
export const SignedOut: Story = { args: { signedOut: true } }
