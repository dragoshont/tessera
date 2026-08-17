export const MaximumDisplayCharacters = 32_000

export function boundedDisplayText(value: string | null | undefined, fallback: string): { text: string; truncated: boolean } {
  const text = value || fallback
  if (text.length <= MaximumDisplayCharacters) return { text, truncated: false }
  return { text: `${text.slice(0, MaximumDisplayCharacters)}\n…`, truncated: true }
}

export function trustedHttpsUrl(value: string): string | null {
  try {
    const url = new URL(value)
    if (url.protocol !== 'https:' || url.username || url.password) return null
    return url.toString()
  } catch {
    return null
  }
}