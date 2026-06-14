import type { Meta, StoryObj } from '@storybook/react-vite'
import { people } from '../../data/fixtures'
import { UsersList } from './UsersList'

const meta = {
  title: 'Users/UsersList',
  component: UsersList,
  args: { people, currentPrincipal: 'alice@example.com' },
} satisfies Meta<typeof UsersList>

export default meta
type Story = StoryObj<typeof meta>

// Acceptance: the operator shows as Admin, the other two as Members.
export const AdminAndMembers: Story = {}
export const SingleAdmin: Story = {
  args: { people: people.filter((person) => person.role === 'Admin') },
}
