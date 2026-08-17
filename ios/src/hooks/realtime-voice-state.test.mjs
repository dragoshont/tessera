import assert from 'node:assert/strict'
import test from 'node:test'

import { boundedRealtimeCaption, classifyRealtimeStartFailure, handoffVoiceToAction, MaximumRealtimeCaptionCharacters } from './realtime-voice-state.ts'

test('maps microphone denial to a bounded permission code', () => {
  assert.deepEqual(classifyRealtimeStartFailure(new Error('Permission denied by AVAudioSession')), {
    state: 'PERMISSION_DENIED',
    code: 'realtime_permission_denied',
  })
})

test('does not expose arbitrary native or provider error messages', () => {
  const privateMessage = 'native failure at file:///private/user/path?token=secret'
  const result = classifyRealtimeStartFailure(new Error(privateMessage))
  assert.deepEqual(result, { state: 'ERROR', code: 'realtime_start_failed' })
  assert.equal(JSON.stringify(result).includes(privateMessage), false)
})

test('maps non-error throws to a bounded failure code', () => {
  assert.deepEqual(classifyRealtimeStartFailure({ private: 'detail' }), {
    state: 'ERROR',
    code: 'realtime_start_failed',
  })
})

test('ends voice before navigating to Action review', async () => {
  const events = []
  await handoffVoiceToAction(async () => { events.push('ended') }, () => { events.push('navigated') })
  assert.deepEqual(events, ['ended', 'navigated'])
})

test('bounds provider transcript captions before display', () => {
  assert.equal(boundedRealtimeCaption('x'.repeat(MaximumRealtimeCaptionCharacters + 100)).length, MaximumRealtimeCaptionCharacters)
  assert.equal(boundedRealtimeCaption(`${'x'.repeat(MaximumRealtimeCaptionCharacters)}delta`).length, MaximumRealtimeCaptionCharacters)
})