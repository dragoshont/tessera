import assert from 'node:assert/strict'
import test from 'node:test'

import { actionReviewState, formatActionPreview, MaximumActionPreviewCharacters } from './action-review-state.ts'

test('expires proposed actions at their canonical deadline', () => {
  assert.equal(actionReviewState('PROPOSED', '2026-01-01T00:00:00Z', Date.parse('2026-01-02T00:00:00Z')), 'EXPIRED')
  assert.equal(actionReviewState('PROPOSED', '2026-01-03T00:00:00Z', Date.parse('2026-01-02T00:00:00Z')), 'PROPOSED')
})

test('does not rewrite terminal server states', () => {
  assert.equal(actionReviewState('COMPLETED', '2026-01-01T00:00:00Z', Date.parse('2026-01-02T00:00:00Z')), 'COMPLETED')
  assert.equal(actionReviewState('PROPOSED', null, Date.parse('2026-01-02T00:00:00Z')), 'PROPOSED')
})

test('bounds untrusted payload previews', () => {
  const preview = formatActionPreview({ text: 'x'.repeat(MaximumActionPreviewCharacters + 100) })
  assert.equal(preview.truncated, true)
  assert.ok(preview.text.length <= MaximumActionPreviewCharacters + 2)
})

test('handles payloads that cannot be serialized', () => {
  const value = {}; value.self = value
  assert.deepEqual(formatActionPreview(value), { text: 'Payload preview is unavailable.', truncated: false })
})