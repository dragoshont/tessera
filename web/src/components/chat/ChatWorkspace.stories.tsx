import { Fragment, createElement } from 'react'
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

const voiceTurns = [{ id: '1', role: 'user' as const, text: 'Let’s plan the release.' }]
export const VoiceReady: Story = { args: { title: 'Release planning', turns: voiceTurns, voice: { state: 'IDLE' } } }
export const VoiceUnavailable: Story = { args: { title: 'Release planning', turns: voiceTurns, voice: { state: 'UNAVAILABLE', blockedCode: 'The gpt-realtime-2.1 deployment is not configured.' } } }
export const VoiceRequestingPermission: Story = { args: { title: 'Release planning', turns: voiceTurns, voice: { state: 'REQUESTING_PERMISSION' } } }
export const VoicePermissionDenied: Story = { args: { title: 'Release planning', turns: voiceTurns, voice: { state: 'PERMISSION_DENIED' } } }
export const VoiceNegotiating: Story = { args: { title: 'Release planning', turns: voiceTurns, voice: { state: 'NEGOTIATING' } } }
export const VoiceListening: Story = { args: { title: 'Release planning', turns: voiceTurns, voice: { state: 'LISTENING', userCaption: 'What should we ship first?' } } }
export const VoiceUserSpeaking: Story = { args: { title: 'Release planning', turns: voiceTurns, voice: { state: 'USER_SPEAKING', userCaption: 'What should we ship first?' } } }
export const VoiceAssistantSpeaking: Story = { args: { title: 'Release planning', turns: voiceTurns, voice: { state: 'ASSISTANT_SPEAKING', userCaption: 'What should we ship first?', assistantCaption: 'Start with the owner-scoped canary.' } } }
export const VoiceAssistantSpeakingMobile: Story = {
  ...VoiceAssistantSpeaking,
  parameters: { viewport: { defaultViewport: 'mobile1' } },
}
export const VoiceAssistantSpeakingDark: Story = {
  ...VoiceAssistantSpeaking,
  globals: { theme: 'dark' },
}
export const VoiceAssistantSpeakingReducedMotion: Story = {
  ...VoiceAssistantSpeaking,
  decorators: [(Story) => createElement(Fragment, null,
    createElement('style', null, '* { animation-duration: 0s !important; transition-duration: 0s !important; }'),
    createElement(Story),
  )],
}
export const VoiceMuted: Story = { args: { title: 'Release planning', turns: voiceTurns, voice: { state: 'LISTENING', muted: true } } }
export const VoiceToolRunning: Story = { args: { title: 'Release planning', turns: voiceTurns, voice: { state: 'TOOL_RUNNING', toolName: 'GitHub issue search' } } }
export const VoiceApprovalRequired: Story = { args: { title: 'Release planning', turns: voiceTurns, voice: { state: 'APPROVAL_REQUIRED', toolName: 'GitHub issue create' } } }
export const VoiceInterrupted: Story = { args: { title: 'Release planning', turns: voiceTurns, voice: { state: 'INTERRUPTED' } } }
export const VoiceExpired: Story = { args: { title: 'Release planning', turns: voiceTurns, voice: { state: 'SESSION_EXPIRED' } } }
export const VoiceAudioBlocked: Story = { args: { title: 'Release planning', turns: voiceTurns, voice: { state: 'AUDIO_OUTPUT_BLOCKED' } } }
export const VoiceError: Story = { args: { title: 'Release planning', turns: voiceTurns, voice: { state: 'ERROR' } } }