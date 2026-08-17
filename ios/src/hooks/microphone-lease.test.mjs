import assert from 'node:assert/strict'
import test from 'node:test'

import { closeMicrophoneCapture, MicrophoneLease } from './microphone-lease.ts'

test('grants the microphone to only one capture mode at a time', () => {
  const lease = new MicrophoneLease()
  assert.equal(lease.acquire('dictation'), true)
  assert.equal(lease.acquire('realtime-voice'), false)
  assert.equal(lease.acquire('dictation'), false)
})

test('only the owner can release the microphone', () => {
  const lease = new MicrophoneLease()
  assert.equal(lease.acquire('dictation'), true)
  lease.release('realtime-voice')
  assert.equal(lease.acquire('realtime-voice'), false)
  lease.release('dictation')
  assert.equal(lease.acquire('realtime-voice'), true)
})

test('supports sequential dictation and realtime voice sessions', () => {
  const lease = new MicrophoneLease()
  assert.equal(lease.acquire('realtime-voice'), true)
  lease.release('realtime-voice')
  assert.equal(lease.acquire('dictation'), true)
})

test('local teardown releases realtime voice without requiring a captured turn', () => {
  const lease = new MicrophoneLease()
  assert.equal(lease.acquire('realtime-voice'), true)
  closeMicrophoneCapture(lease, 'realtime-voice', () => {})
  assert.equal(lease.acquire('dictation'), true)
})

test('local teardown releases the microphone even when media cleanup throws', () => {
  const lease = new MicrophoneLease()
  assert.equal(lease.acquire('realtime-voice'), true)
  assert.throws(() => closeMicrophoneCapture(lease, 'realtime-voice', () => { throw new Error('track cleanup failed') }))
  assert.equal(lease.acquire('dictation'), true)
})