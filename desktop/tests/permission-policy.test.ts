import { describe, expect, it } from 'vitest'
import { isTrustedAudioPermission } from '../src/permission-policy'

describe('Electron audio permission policy', () => {
  it('allows only main-frame audio from the trusted app origin', () => {
    expect(isTrustedAudioPermission({ permission: 'media', requestingUrl: 'app://tessera/chat', securityOrigin: 'app://tessera', isMainFrame: true, mediaTypes: ['audio'] })).toBe(true)
    expect(isTrustedAudioPermission({ permission: 'media', requestingUrl: 'app://tessera/chat', isMainFrame: true, mediaTypes: ['video'] })).toBe(false)
    expect(isTrustedAudioPermission({ permission: 'media', requestingUrl: 'app://tessera/chat', isMainFrame: true, mediaTypes: ['audio', 'video'] })).toBe(false)
    expect(isTrustedAudioPermission({ permission: 'media', requestingUrl: 'app://tessera/chat', isMainFrame: false, mediaTypes: ['audio'] })).toBe(false)
    expect(isTrustedAudioPermission({ permission: 'media', requestingUrl: 'https://tessera.hont.ro/chat', isMainFrame: true, mediaTypes: ['audio'] })).toBe(false)
    expect(isTrustedAudioPermission({ permission: 'geolocation', requestingUrl: 'app://tessera/', isMainFrame: true, mediaTypes: ['audio'] })).toBe(false)
  })
})