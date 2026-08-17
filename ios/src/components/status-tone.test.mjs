import assert from 'node:assert/strict'
import test from 'node:test'

import { statusTone } from './status-tone.ts'

test('maps known positive states to success', () => {
  assert.equal(statusTone('READY'), 'success')
  assert.equal(statusTone('CONNECTED'), 'success')
})

test('maps pending and approval-required states to warning', () => {
  assert.equal(statusTone('PROPOSED'), 'warning')
  assert.equal(statusTone('APPROVAL_REQUIRED'), 'warning')
  assert.equal(statusTone('NOT_DETERMINED'), 'warning')
})

test('maps denied and unavailable states to danger', () => {
  assert.equal(statusTone('PERMISSION_DENIED'), 'danger')
  assert.equal(statusTone('UNAVAILABLE'), 'danger')
})

test('never renders an unrecognized status as success', () => {
  assert.equal(statusTone('unknown'), 'neutral')
  assert.equal(statusTone('FUTURE_SERVER_STATE'), 'neutral')
})