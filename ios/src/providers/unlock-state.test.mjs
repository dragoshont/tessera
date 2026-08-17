import assert from 'node:assert/strict'
import test from 'node:test'

import { unlockTransition } from './unlock-state.ts'

test('keeps an in-flight Face ID attempt through the transient inactive sheet', () => {
  assert.equal(unlockTransition('inactive', { generation: 4, authenticatedAt: null }, true, 1_000), 'IGNORE')
})

test('completes a recent successful attempt when the app becomes active', () => {
  assert.equal(unlockTransition('active', { generation: 4, authenticatedAt: 1_000 }, true, 5_000), 'COMPLETE')
})

test('does not unlock a stale or invalidated attempt', () => {
  assert.equal(unlockTransition('active', { generation: 4, authenticatedAt: 1_000 }, true, 12_000), 'IGNORE')
  assert.equal(unlockTransition('active', { generation: 4, authenticatedAt: 1_000 }, false, 2_000), 'IGNORE')
})

test('fails closed when the app actually backgrounds during Face ID', () => {
  assert.equal(unlockTransition('background', { generation: 4, authenticatedAt: null }, true, 1_000), 'LOCK')
})

test('locks ordinary non-active transitions when no authentication is running', () => {
  assert.equal(unlockTransition('inactive', null, true, 1_000), 'LOCK')
  assert.equal(unlockTransition('background', null, true, 1_000), 'LOCK')
})