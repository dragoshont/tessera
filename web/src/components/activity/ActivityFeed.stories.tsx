import type { Meta, StoryObj } from '@storybook/react-vite'
import type { AuditFeed } from '../../data/types'
import { auditRows } from '../../data/fixtures'
import { ActivityFeed } from './ActivityFeed'

const ordered = [...auditRows].sort((a, b) => b.timestamp.localeCompare(a.timestamp))

const feed: AuditFeed = {
  entries: ordered,
  summary: {
    total: ordered.length,
    allow: ordered.filter((r) => r.effect === 'allow').length,
    deny: ordered.filter((r) => r.effect === 'deny').length,
    stepUp: ordered.filter((r) => r.effect === 'step-up').length,
    byTarget: {},
    byCaller: {},
    since: ordered.at(-1)?.timestamp ?? null,
    until: ordered.at(0)?.timestamp ?? null,
  },
}

const meta = {
  title: 'Awareness/ActivityFeed',
  component: ActivityFeed,
  parameters: { layout: 'padded' },
} satisfies Meta<typeof ActivityFeed>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = { args: { feed } }

export const Loading: Story = { args: { isLoading: true } }

export const Empty: Story = {
  args: {
    feed: {
      entries: [],
      summary: { total: 0, allow: 0, deny: 0, stepUp: 0, byTarget: {}, byCaller: {}, since: null, until: null },
    },
    emptyHint: 'No activity on your behalf yet.',
  },
}
