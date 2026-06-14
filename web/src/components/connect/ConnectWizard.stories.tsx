import type { Meta, StoryObj } from '@storybook/react-vite'
import type { Connection, CreateConnectionInput } from '../../data/types'
import { demoLiveViewHandle, people, recipes } from '../../data/fixtures'
import { ConnectWizard } from './ConnectWizard'

// A synthesized "created" binding for the happy-path action (Storybook has no backend).
const created = (input: CreateConnectionInput): Connection => ({
  connectionId: `${input.provider}:${input.principal}`,
  ownerPrincipal: input.principal,
  provider: input.provider,
  displayName: input.provider,
  status: 'absent',
  expiryIsEstimated: false,
  hasCookies: false,
  hasRefreshToken: false,
  hasAccessToken: false,
})

const meta = {
  title: 'Connect/ConnectWizard',
  component: ConnectWizard,
  parameters: { layout: 'padded' },
  args: {
    recipes,
    currentPrincipal: 'alice@example.com',
    isAdmin: true,
    people,
    // Fail-closed by default — the calm "not configured yet" path is the norm now.
    requestSeed: async () => ({
      unavailable: 'live hand-off is not configured: no browser worker is wired (fail-closed).',
    }),
    createConnection: async (input) => created(input),
    onCreated: () => undefined,
    onCancel: () => undefined,
    onOpenHandoff: () => undefined,
  },
} satisfies Meta<typeof ConnectWizard>

export default meta
type Story = StoryObj<typeof meta>

const draft = {
  provider: 'health',
  displayName: 'Health Portal',
  principal: 'bob@example.com',
  credential: 'health-portal-bob-session',
}

export const Step1Provider: Story = {}

export const Step1ProvidersLoading: Story = { args: { recipesLoading: true } }

export const Step1ProvidersError: Story = { args: { recipesError: true } }

export const Step2PersonAdmin: Story = {
  args: { initialState: { step: 'person', draft: { provider: 'health', displayName: 'Health Portal' } } },
}

export const Step2PersonMember: Story = {
  args: {
    isAdmin: false,
    people: undefined,
    currentPrincipal: 'bob@example.com',
    initialState: { step: 'person', draft: { provider: 'health', displayName: 'Health Portal' } },
  },
}

export const Step3Credential: Story = {
  args: {
    initialState: {
      step: 'credential',
      draft: { provider: 'health', displayName: 'Health Portal', principal: 'bob@example.com' },
    },
  },
}

export const Step4SeedUnavailable: Story = {
  args: {
    initialState: {
      step: 'seed',
      draft,
      seed: {
        phase: 'unavailable',
        reason: 'live hand-off is not configured: no browser worker is wired (fail-closed).',
      },
    },
  },
}

export const Step4SeedReady: Story = {
  args: {
    initialState: { step: 'seed', draft, seed: { phase: 'ready', handle: demoLiveViewHandle } },
  },
}

export const Step5Finish: Story = {
  args: { initialState: { step: 'finish', draft } },
}

export const Step5FinishError: Story = {
  args: {
    initialState: {
      step: 'finish',
      draft,
      submitError: 'You don’t have permission to connect this account for that person.',
    },
  },
}
