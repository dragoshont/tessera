import assert from 'node:assert/strict'
import test from 'node:test'

import { boundedDisplayText, MaximumDisplayCharacters, trustedHttpsUrl } from './display-boundary.ts'

test('bounds untrusted output text in the client', () => {
  const value = boundedDisplayText('x'.repeat(MaximumDisplayCharacters + 100), 'No output.')
  assert.equal(value.truncated, true)
  assert.ok(value.text.length <= MaximumDisplayCharacters + 2)
})

test('uses an explicit fallback for missing output', () => {
  assert.deepEqual(boundedDisplayText(null, 'No output.'), { text: 'No output.', truncated: false })
})

test('accepts credential-free HTTPS inspect URLs only', () => {
  assert.equal(trustedHttpsUrl('https://example.com/package'), 'https://example.com/package')
  assert.equal(trustedHttpsUrl('http://example.com/package'), null)
  assert.equal(trustedHttpsUrl('https://user:secret@example.com/package'), null)
  assert.equal(trustedHttpsUrl('not a url'), null)
})