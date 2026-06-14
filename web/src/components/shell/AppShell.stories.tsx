import type { Decorator, Meta, StoryObj } from '@storybook/react-vite'
import type { Person } from '../../data/types'
import { aliceConnections, people } from '../../data/fixtures'
import { SessionProvider } from '../../app/session'
import { ThemeProvider } from '../theme/theme-provider'
import { AccountsTable } from '../accounts/AccountsTable'
import { AppShell } from './AppShell'

const admin = people.find((person) => person.role === 'Admin') ?? people[0]
const member = people.find((person) => person.role === 'Member') ?? people[0]

const withSession =
  (user: Person): Decorator =>
  (Story) => (
    <ThemeProvider>
      <SessionProvider initialUser={user}>
        <Story />
      </SessionProvider>
    </ThemeProvider>
  )

const sampleContent = (
  <AccountsTable connections={aliceConnections} ownerPrincipal="alice@example.com" />
)

const meta = {
  title: 'Shell/AppShell',
  component: AppShell,
  parameters: { layout: 'fullscreen' },
} satisfies Meta<typeof AppShell>

export default meta
type Story = StoryObj<typeof meta>

export const AdminNav: Story = {
  decorators: [withSession(admin)],
  args: { children: sampleContent },
}

export const MemberNav: Story = {
  decorators: [withSession(member)],
  args: { children: sampleContent },
}
