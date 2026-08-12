import type { Meta, StoryObj } from '@storybook/react-vite'
import type { FollowUpDetail, FollowUpSummary } from '../../data/types'
import { FollowUpWorkspace } from './FollowUpWorkspace'

const revisions: FollowUpDetail['revisions'] = [
  {
    revisionId: 'revision-deliverable-corrected',
    field: 'deliverable',
    value: 'lease renewal checklist',
    state: 'current',
    evidenceRefs: ['evidence-correction'],
    sourceTimestamp: '2026-08-10T11:00:00Z',
    parserVersion: '1',
    confidence: 1,
    correctionEvidenceRef: 'evidence-correction',
    lineageRevisionRefs: ['revision-deliverable-original'],
    createdAt: '2026-08-10T11:00:00Z',
  },
  {
    revisionId: 'revision-counterparty',
    field: 'counterparty',
    value: 'Rowan',
    state: 'current',
    evidenceRefs: ['evidence-initial'],
    sourceTimestamp: '2026-08-10T09:00:00Z',
    parserVersion: 'followup.fixture.v1',
    confidence: 0.99,
    correctionEvidenceRef: null,
    lineageRevisionRefs: [],
    createdAt: '2026-08-10T09:01:00Z',
  },
  {
    revisionId: 'revision-monday',
    field: 'dueAt',
    value: '2026-08-17',
    state: 'candidate',
    evidenceRefs: ['evidence-monday'],
    sourceTimestamp: '2026-08-11T09:00:00Z',
    parserVersion: 'followup.fixture.v1',
    confidence: 0.95,
    correctionEvidenceRef: null,
    lineageRevisionRefs: ['revision-deliverable-corrected', 'revision-counterparty'],
    createdAt: '2026-08-11T09:01:00Z',
  },
]

const timeline: FollowUpDetail['timeline'] = [
  {
    sequence: 1,
    kind: 'Imported',
    field: null,
    summary: 'Imported deterministic source evidence as candidate state.',
    evidenceRef: 'evidence-initial',
    sourceTimestamp: '2026-08-10T09:00:00Z',
    recordedAt: '2026-08-10T09:01:00Z',
  },
  {
    sequence: 2,
    kind: 'Corrected',
    field: 'deliverable',
    summary: 'Corrected Deliverable.',
    evidenceRef: 'evidence-correction',
    sourceTimestamp: '2026-08-10T11:00:00Z',
    recordedAt: '2026-08-10T11:00:00Z',
  },
]

const attentionSummary: FollowUpSummary = {
  followUpId: 'followup:r1-lease-rowan',
  status: 'attention',
  version: 4,
  deliverable: 'lease renewal checklist',
  counterparty: 'Rowan',
  dueAt: '2026-08-14',
  candidateCount: 1,
  conflictCount: 0,
  updatedAt: '2026-08-11T09:01:00Z',
}

const attentionDetail: FollowUpDetail = {
  followUpId: attentionSummary.followUpId,
  status: 'attention',
  version: attentionSummary.version,
  createdAt: '2026-08-10T09:01:00Z',
  updatedAt: attentionSummary.updatedAt,
  revisions,
  timeline,
  timelineTruncated: false,
}

const conflictRevisions = revisions.map((revision) =>
  revision.field === 'dueAt' ? { ...revision, state: 'conflicted' as const } : revision)
conflictRevisions.push({
  ...revisions[2],
  revisionId: 'revision-conflicting-friday',
  value: '2026-08-14',
  state: 'conflicted',
  confidence: 0.99,
})

const meta = {
  title: 'Continuity/FollowUpWorkspace',
  component: FollowUpWorkspace,
  parameters: { layout: 'padded', a11y: { test: 'error' } },
} satisfies Meta<typeof FollowUpWorkspace>

export default meta
type Story = StoryObj<typeof meta>

export const Attention: Story = {
  args: { view: 'attention', items: [attentionSummary], selected: attentionDetail },
}

export const Conflict: Story = {
  args: {
    view: 'attention',
    items: [{ ...attentionSummary, status: 'conflict', candidateCount: 0, conflictCount: 2 }],
    selected: { ...attentionDetail, status: 'conflict', revisions: conflictRevisions },
  },
}

export const Tracked: Story = {
  args: {
    view: 'tracked',
    items: [{ ...attentionSummary, status: 'tracked', candidateCount: 0 }],
    selected: { ...attentionDetail, status: 'tracked', revisions: revisions.map((revision) => ({ ...revision, state: 'current' })) },
  },
}

export const Completed: Story = {
  args: {
    view: 'tracked',
    items: [{ ...attentionSummary, status: 'completed', candidateCount: 0 }],
    selected: { ...attentionDetail, status: 'completed' },
  },
}

export const Loading: Story = { args: { view: 'attention', isLoading: true } }
export const Empty: Story = { args: { view: 'attention', items: [] } }
export const Error: Story = {
  args: { view: 'attention', items: [attentionSummary], errorMessage: 'Continuity could not be loaded. Retry.' },
}