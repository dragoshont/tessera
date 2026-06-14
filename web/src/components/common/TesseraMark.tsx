export function TesseraMark({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 32 32" className={className} role="img" aria-label="Tessera">
      <rect width="32" height="32" rx="7" fill="currentColor" />
      <g fill="#ffffff" fillOpacity={0.95}>
        <rect x="7" y="7" width="7" height="7" rx="1.5" />
        <rect x="18" y="7" width="7" height="7" rx="1.5" fillOpacity={0.6} />
        <rect x="7" y="18" width="7" height="7" rx="1.5" fillOpacity={0.6} />
        <rect x="18" y="18" width="7" height="7" rx="1.5" />
      </g>
    </svg>
  )
}
