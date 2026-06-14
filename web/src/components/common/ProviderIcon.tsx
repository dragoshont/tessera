import { cn } from '../../lib/utils'

// Calm provider glyphs. We deliberately do NOT render a remote favicon in Phase 0:
// the anti-phishing target strip needs a server-verified, non-spoofable favicon
// (labeled backend gap), so until then we degrade to a quiet local tile.
const PROVIDER_EMOJI: Record<string, string> = {
  'Health Portal': '🏥',
  'Utility Co': '🏦',
  Marketplace: '🛒',
  Webmail: '📨',
}

export function ProviderIcon({
  provider,
  className,
}: {
  provider: string
  className?: string
}) {
  const emoji = PROVIDER_EMOJI[provider]
  return (
    <span
      aria-hidden
      className={cn(
        'flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border border-border bg-muted text-base',
        className,
      )}
    >
      {emoji ?? provider[0]?.toUpperCase() ?? '?'}
    </span>
  )
}
