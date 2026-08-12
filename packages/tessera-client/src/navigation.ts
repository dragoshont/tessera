const STATIC_PATHS = new Set([
  '/(tabs)/chat',
  '/(tabs)/jobs',
  '/(tabs)/accounts',
  '/(tabs)/memory',
  '/(tabs)/more',
  '/plugins',
  '/activity',
  '/settings',
])

export function isAllowedAppPath(value: unknown): value is string {
  if (typeof value !== 'string' || value.length > 180 || value.includes('..') || value.includes('%') || value.includes('?') || value.includes('#')) return false
  return STATIC_PATHS.has(value) || /^\/action\/[A-Za-z0-9_-]{1,128}$/.test(value)
}