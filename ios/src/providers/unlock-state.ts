export type UnlockAttempt = { generation: number; authenticatedAt: number | null }
export type UnlockTransition = 'COMPLETE' | 'IGNORE' | 'LOCK'

export function unlockTransition(
  appState: string,
  attempt: UnlockAttempt | null,
  generationCurrent: boolean,
  now: number,
): UnlockTransition {
  if (appState === 'active') {
    return attempt?.authenticatedAt && generationCurrent && now - attempt.authenticatedAt <= 10_000
      ? 'COMPLETE'
      : 'IGNORE'
  }
  if (appState === 'inactive' && attempt && generationCurrent) return 'IGNORE'
  return 'LOCK'
}