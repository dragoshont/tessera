import type { Meta, StoryObj } from '@storybook/react-vite'
import { ChatWorkspace } from './ChatWorkspace'

const meta = { title: 'Product/ChatWorkspace', component: ChatWorkspace, parameters: { layout: 'padded', a11y: { test: 'error' } } } satisfies Meta<typeof ChatWorkspace>
export default meta
type Story = StoryObj<typeof meta>

export const Empty: Story = { args: { turns: [] } }
export const Configured: Story = { args: { title: 'Morning planning', turns: [
  { id: '1', role: 'user', text: 'What needs my attention today?' },
  { id: '2', role: 'assistant', text: 'Two FollowUps are due this week. No external action was taken.', status: 'Completed' },
] } }
export const Loading: Story = { args: { loading: true } }
export const Error: Story = { args: { turns: [{ id: '1', role: 'user', text: 'Summarize my open issues.' }], errorMessage: 'The model provider is unavailable. Your message was saved; retry when the connection recovers.' } }
export const ConfigurationRequired: Story = { args: { configurationRequired: true } }