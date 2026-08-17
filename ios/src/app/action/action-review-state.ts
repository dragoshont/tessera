export const MaximumActionPreviewCharacters = 16_000

export function actionReviewState(state: string, expiresAt: string | null | undefined, now = Date.now()): string {
  if (state !== 'PROPOSED' || !expiresAt) return state
  const expiry = new Date(expiresAt).valueOf()
  return Number.isFinite(expiry) && expiry <= now ? 'EXPIRED' : state
}

export function formatActionPreview(payload: unknown): { text: string; truncated: boolean } {
  let text: string
  try { text = JSON.stringify(payload, null, 2) ?? 'No payload' }
  catch { text = 'Payload preview is unavailable.' }
  if (text.length <= MaximumActionPreviewCharacters) return { text, truncated: false }
  return { text: `${text.slice(0, MaximumActionPreviewCharacters)}\n…`, truncated: true }
}