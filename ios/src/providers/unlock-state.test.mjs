import assert from 'node:assert/strict'
import test from 'node:test'

import { hasActiveForegroundSystemInteraction, unlockTransition } from './unlock-state.ts'

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

test('ignores inactive while a foreground system permission sheet is open', () => {
  assert.equal(unlockTransition('inactive', null, true, 1_000, true), 'IGNORE')
})

test('still locks real background transitions during a system interaction', () => {
  assert.equal(unlockTransition('background', null, true, 1_000, true), 'LOCK')
})

test('expires abandoned foreground system interactions', () => {
  assert.equal(hasActiveForegroundSystemInteraction([900, 999], 1_000), false)
  assert.equal(hasActiveForegroundSystemInteraction([900, 1_001], 1_000), true)
})