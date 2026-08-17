import assert from 'node:assert/strict'
import test from 'node:test'

import { notificationPermissionState } from './notification-state.ts'

test('models every iOS notification authorization state', () => {
  assert.equal(notificationPermissionState({ granted: false, canAskAgain: true, ios: { status: 0 } }).label, 'NOT_DETERMINED')
  assert.equal(notificationPermissionState({ granted: false, canAskAgain: false, ios: { status: 1 } }).label, 'DENIED')
  assert.equal(notificationPermissionState({ granted: true, canAskAgain: true, ios: { status: 2 } }).label, 'AUTHORIZED')
  assert.equal(notificationPermissionState({ granted: true, canAskAgain: true, ios: { status: 3 } }).label, 'PROVISIONAL')
  assert.equal(notificationPermissionState({ granted: true, canAskAgain: true, ios: { status: 4 } }).label, 'EPHEMERAL')
})

test('treats authorized provisional and ephemeral states as usable', () => {
  assert.equal(notificationPermissionState({ granted: true, canAskAgain: true, ios: { status: 2 } }).usable, true)
  assert.equal(notificationPermissionState({ granted: true, canAskAgain: true, ios: { status: 3 } }).usable, true)
  assert.equal(notificationPermissionState({ granted: true, canAskAgain: true, ios: { status: 4 } }).usable, true)
})

test('preserves canAskAgain for Settings recovery decisions', () => {
  assert.equal(notificationPermissionState({ granted: false, canAskAgain: false, ios: { status: 1 } }).canAskAgain, false)
})