import type { Meta, StoryObj } from '@storybook/react-vite'
import { DevelopmentTaskCreator } from './R2ProductComponents'

const conversations = [{ id: 'conversation-1', conversationId: 'conversation-1', title: 'Tessera development', state: 'ACTIVE' as const, modelProfileId: 'profile-1', createdAt: '2026-08-12T18:00:00Z', updatedAt: '2026-08-12T18:00:00Z', version: 1 }]
const workspaces = [{ id: 'workspace-1', displayName: 'Tessera', snapshotHash: 'sha256:835f28b2f08507e6', state: 'READY' as const, createdAt: '2026-08-12T18:00:00Z', version: 1 }]

const meta = {
  title: 'Product/DevelopmentTaskCreator',
  component: DevelopmentTaskCreator,
  parameters: { layout: 'padded', a11y: { test: 'error' } },
  args: {
    conversations,
    workspaces,
    conversationId: 'conversation-1',
    workspaceId: 'workspace-1',
    onConversationChange: () => undefined,
    onWorkspaceChange: () => undefined,
    onSubmit: () => undefined,
  },
} satisfies Meta<typeof DevelopmentTaskCreator>

export default meta
type Story = StoryObj<typeof meta>

export const Ready: Story = {}
export const LoadingConversations: Story = { args: { conversations: [], workspaces: [], conversationId: '', workspaceId: '', conversationsLoading: true } }
export const LoadingWorkspaces: Story = { args: { workspaces: [], workspaceId: '', workspacesLoading: true } }
export const NoReadyWorkspaces: Story = { args: { workspaces: [], workspaceId: '', onRefresh: () => undefined } }
export const Submitting: Story = { args: { submitting: true } }
export const WorkspaceUnavailable: Story = { args: { errorCode: 'workspace_unavailable' } }
export const ExecutorUnavailable: Story = { args: { errorCode: 'development_executor_unavailable' } }