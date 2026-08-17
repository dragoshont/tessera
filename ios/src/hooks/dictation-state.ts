export const MaximumDraftCharacters = 12_000

export type DictationPermission = 'GRANTED' | 'DENIED' | 'RESTRICTED'
export type DictationFailure = 'PERMISSION_DENIED' | 'RESTRICTED' | 'NO_SPEECH' | 'INTERRUPTED' | 'UNAVAILABLE' | 'ERROR'
export type DictationState = 'IDLE' | 'REQUESTING_PERMISSION' | 'LISTENING' | 'PROCESSING' | DictationFailure
export type DictationPermissionDecision = 'REQUEST' | 'START' | 'PERMISSION_DENIED' | 'RESTRICTED' | 'INTERRUPTED'

export function classifyDictationPermission(permission: {
  granted: boolean
  restricted?: boolean
}): DictationPermission {
  if (permission.restricted) return 'RESTRICTED'
  return permission.granted ? 'GRANTED' : 'DENIED'
}

export function classifyDictationError(code: string): DictationFailure {
  if (code === 'not-allowed') return 'PERMISSION_DENIED'
  if (code === 'no-speech') return 'NO_SPEECH'
  if (code === 'interrupted' || code === 'aborted') return 'INTERRUPTED'
  if (code === 'service-not-allowed' || code === 'language-not-supported' || code === 'audio-capture') return 'UNAVAILABLE'
  return 'ERROR'
}

export function decideDictationPermission(permission: {
  granted: boolean
  restricted?: boolean
  canAskAgain?: boolean
}, mounted = true, appActive = true, enabled = true): DictationPermissionDecision {
  const state = classifyDictationPermission(permission)
  if (state === 'RESTRICTED') return 'RESTRICTED'
  if (state === 'DENIED') return permission.canAskAgain ? 'REQUEST' : 'PERMISSION_DENIED'
  return mounted && appActive && enabled ? 'START' : 'INTERRUPTED'
}

export function nextDictationState(current: DictationState, event: 'START' | 'PARTIAL_RESULT' | 'FINAL_RESULT' | 'END' | 'BACKGROUND'): DictationState {
  if (event === 'START') return 'LISTENING'
  if (event === 'PARTIAL_RESULT') return current === 'PROCESSING' ? current : 'LISTENING'
  if (event === 'FINAL_RESULT') return 'PROCESSING'
  if (event === 'BACKGROUND') return isDictationCapturing(current) ? 'INTERRUPTED' : current
  return current === 'REQUESTING_PERMISSION' || current === 'LISTENING' || current === 'PROCESSING' ? 'IDLE' : current
}

export function isDictationCapturing(state: DictationState): boolean {
  return state === 'REQUESTING_PERMISSION' || state === 'LISTENING' || state === 'PROCESSING'
}

export function isRealtimeVoiceCapturing(state: string): boolean {
  return ['REQUESTING_PERMISSION', 'NEGOTIATING', 'LISTENING', 'USER_SPEAKING', 'ASSISTANT_SPEAKING', 'TOOL_RUNNING', 'APPROVAL_REQUIRED', 'ENDING'].includes(state)
}

export function mergeDictationDraft(baseDraft: string, transcript: string): string {
  const base = baseDraft.slice(0, MaximumDraftCharacters)
  const spoken = transcript.trim()
  if (!spoken) return base
  const separator = base && !/\s$/.test(base) ? ' ' : ''
  return `${base}${separator}${spoken}`.slice(0, MaximumDraftCharacters)
}