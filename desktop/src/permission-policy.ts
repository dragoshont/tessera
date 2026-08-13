import { APP_HOST, APP_SCHEME } from './security'

type PermissionInput = {
  permission: string
  requestingUrl?: string
  securityOrigin?: string
  isMainFrame: boolean
  mediaTypes: string[]
}

export function isTrustedAudioPermission(input: PermissionInput): boolean {
  if (input.permission !== 'media' || !input.isMainFrame || input.mediaTypes.length !== 1 || input.mediaTypes[0] !== 'audio') return false
  const values = [input.requestingUrl, input.securityOrigin].filter((value): value is string => Boolean(value))
  return values.length > 0 && values.every((value) => {
    try {
      const url = new URL(value)
      return url.protocol === APP_SCHEME && url.host === APP_HOST && url.username === '' && url.password === '' && url.port === ''
    } catch { return false }
  })
}