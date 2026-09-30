export type UnlockAttempt = { generation: number; authenticatedAt: number | null }
export type UnlockTransition = 'COMPLETE' | 'IGNORE' | 'LOCK'

export function unlockTransition(
  appState: string,
  attempt: UnlockAttempt | null,
  generationCurrent: boolean,
  now: number,
  foregroundSystemInteraction = false,
): UnlockTransition {
  if (appState === 'active') {
    return attempt?.authenticatedAt && generationCurrent && now - attempt.authenticatedAt <= 10_000
      ? 'COMPLETE'
      : 'IGNORE'
  }
  if (appState === 'inactive' && attempt && generationCurrent) return 'IGNORE'
  if (appState === 'inactive' && foregroundSystemInteraction) return 'IGNORE'
  return 'LOCK'
}

export function hasActiveForegroundSystemInteraction(expirations: Iterable<number>, now: number): boolean {
  for (const expiration of expirations) if (expiration >= now) return true
  return false
}