import assert from 'node:assert/strict'
import test from 'node:test'

import {
  MaximumDraftCharacters,
  classifyDictationError,
  classifyDictationPermission,
  decideDictationPermission,
  isDictationCapturing,
  isRealtimeVoiceCapturing,
  mergeDictationDraft,
  nextDictationState,
} from './dictation-state.ts'

test('classifies granted denied and restricted permissions explicitly', () => {
  assert.equal(classifyDictationPermission({ granted: true }), 'GRANTED')
  assert.equal(classifyDictationPermission({ granted: false }), 'DENIED')
  assert.equal(classifyDictationPermission({ granted: false, restricted: true }), 'RESTRICTED')
})

test('preserves and separates the existing editable draft', () => {
  assert.equal(mergeDictationDraft('Review this', 'tomorrow.'), 'Review this tomorrow.')
  assert.equal(mergeDictationDraft('Review this ', ' tomorrow. '), 'Review this tomorrow.')
})

test('returns the original draft when recognition produced no text', () => {
  assert.equal(mergeDictationDraft('Keep me', '  '), 'Keep me')
})

test('caps provisional and final drafts at the composer limit', () => {
  assert.equal(mergeDictationDraft('x'.repeat(MaximumDraftCharacters), 'more').length, MaximumDraftCharacters)
  assert.equal(mergeDictationDraft('', 'x'.repeat(MaximumDraftCharacters + 50)).length, MaximumDraftCharacters)
})

test('maps interruption and platform failures without exposing native messages', () => {
  assert.equal(classifyDictationError('not-allowed'), 'PERMISSION_DENIED')
  assert.equal(classifyDictationError('no-speech'), 'NO_SPEECH')
  assert.equal(classifyDictationError('interrupted'), 'INTERRUPTED')
  assert.equal(classifyDictationError('service-not-allowed'), 'UNAVAILABLE')
  assert.equal(classifyDictationError('native-private-detail'), 'ERROR')
})

test('requests permission only when the system permits another prompt', () => {
  assert.equal(decideDictationPermission({ granted: false, canAskAgain: true }), 'REQUEST')
  assert.equal(decideDictationPermission({ granted: false, canAskAgain: false }), 'PERMISSION_DENIED')
  assert.equal(decideDictationPermission({ granted: false, restricted: true, canAskAgain: true }), 'RESTRICTED')
})

test('will not begin capture when permission resolves after background or unmount', () => {
  assert.equal(decideDictationPermission({ granted: true }, true, true), 'START')
  assert.equal(decideDictationPermission({ granted: true }, true, false), 'INTERRUPTED')
  assert.equal(decideDictationPermission({ granted: true }, false, true), 'INTERRUPTED')
  assert.equal(decideDictationPermission({ granted: true }, true, true, false), 'INTERRUPTED')
})

test('models native event ordering and preserves terminal failures', () => {
  assert.equal(nextDictationState('REQUESTING_PERMISSION', 'START'), 'LISTENING')
  assert.equal(nextDictationState('LISTENING', 'PARTIAL_RESULT'), 'LISTENING')
  assert.equal(nextDictationState('LISTENING', 'FINAL_RESULT'), 'PROCESSING')
  assert.equal(nextDictationState('PROCESSING', 'END'), 'IDLE')
  assert.equal(nextDictationState('REQUESTING_PERMISSION', 'END'), 'IDLE')
  assert.equal(nextDictationState('NO_SPEECH', 'END'), 'NO_SPEECH')
})

test('keeps processing state when a buffered partial result arrives after stop', () => {
  assert.equal(nextDictationState('PROCESSING', 'PARTIAL_RESULT'), 'PROCESSING')
})

test('background interruption applies only while capture owns the microphone', () => {
  assert.equal(nextDictationState('LISTENING', 'BACKGROUND'), 'INTERRUPTED')
  assert.equal(nextDictationState('IDLE', 'BACKGROUND'), 'IDLE')
  assert.equal(isDictationCapturing('REQUESTING_PERMISSION'), true)
  assert.equal(isDictationCapturing('INTERRUPTED'), false)
})

test('dictation and realtime voice expose reciprocal microphone ownership', () => {
  assert.equal(isRealtimeVoiceCapturing('NEGOTIATING'), true)
  assert.equal(isRealtimeVoiceCapturing('ASSISTANT_SPEAKING'), true)
  assert.equal(isRealtimeVoiceCapturing('IDLE'), false)
  assert.equal(isRealtimeVoiceCapturing('PERMISSION_DENIED'), false)
})