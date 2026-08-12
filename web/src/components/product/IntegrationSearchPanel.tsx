import { useState } from 'react'
import type { R2IntegrationCatalogItem, R2IntegrationSource } from '../../api/r2'
import { Alert, AlertDescription } from '../ui/alert'
import { Badge } from '../ui/badge'
import { Button } from '../ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '../ui/dialog'
import { Input } from '../ui/input'
import { ProductStateBadge } from './R2ProductComponents'

export function IntegrationSearchPanel({ query, items, sources, loading, installingId, errorMessage, onSearch, onInspect, onInstall }: {
  query: string
  items: R2IntegrationCatalogItem[]
  sources: R2IntegrationSource[]
  loading: boolean
  installingId: string | null
  errorMessage: string | null
  onSearch: (query: string) => void
  onInspect: (url: string) => void
  onInstall: (item: R2IntegrationCatalogItem) => void
}) {
  const [searchText, setSearchText] = useState(query)
  const [review, setReview] = useState<R2IntegrationCatalogItem | null>(null)
  return (
    <section className="border-t border-border py-6" aria-labelledby="integration-search-title">
      <h2 id="integration-search-title" className="text-lg font-semibold">Find integrations</h2>
      <p className="mt-1 text-sm text-muted-foreground">Search installed integrations, the official MCP Registry, and public MCP repositories. Search results are metadata only; Tessera never downloads or executes unreviewed code.</p>
      <form className="mt-4 flex flex-col gap-2 sm:flex-row" onSubmit={(event) => { event.preventDefault(); const value = searchText.trim(); if (value.length >= 2) onSearch(value) }}>
        <Input aria-label="Search integrations" placeholder="Search integrations…" value={searchText} minLength={2} maxLength={100} onChange={(event) => setSearchText(event.target.value)} />
        <Button disabled={searchText.trim().length < 2 || loading}>{loading ? 'Searching…' : 'Search'}</Button>
      </form>
      <div className="mt-3 flex flex-wrap gap-2" aria-label="Catalog sources">{sources.map((source) => <Badge key={source.id} variant="outline">{source.name}: {source.state.toLowerCase()}</Badge>)}</div>
      {errorMessage ? <Alert variant="destructive" className="mt-4"><AlertDescription>{errorMessage}</AlertDescription></Alert> : null}
      {query && !loading && items.length === 0 ? <p className="py-8 text-center text-sm text-muted-foreground">No compatible public or local integrations matched “{query}”.</p> : null}
      {items.length ? <ul className="mt-5 divide-y divide-border border-y border-border">{items.map((item) => <li key={`${item.source}:${item.id}:${item.version}`} className="py-4">
        <div className="flex flex-wrap items-start justify-between gap-3"><div className="min-w-0 flex-1"><p className="font-medium">{item.name}</p><p className="mt-1 text-sm text-muted-foreground">{item.description}</p><p className="mt-2 text-xs text-muted-foreground">{item.source} · {item.publisher} · {item.runtime} · {item.version}{item.license ? ` · ${item.license}` : ''}</p></div><div className="flex flex-wrap gap-2"><ProductStateBadge state={item.installState} /><ProductStateBadge state={item.trustLevel} /><ProductStateBadge state={item.sensitivity} /></div></div>
        {item.capabilitiesSummary.length ? <p className="mt-3 text-sm">Capabilities: {item.capabilitiesSummary.join(' · ')}</p> : null}
        {item.authTypes.length ? <Alert className="mt-3"><AlertDescription>Authorization: {item.authTypes.join(', ')}. Review where credentials and sensitive data would be sent before installation.</AlertDescription></Alert> : null}
        <div className="mt-3 flex flex-wrap items-center gap-2">{item.inspectUrl ? <Button size="sm" variant="outline" onClick={() => onInspect(item.inspectUrl!)}>Inspect source</Button> : item.source === 'local' ? <span className="text-xs text-muted-foreground">Built into the reviewed Tessera server image.</span> : <span className="text-xs text-muted-foreground">Public source URL unavailable.</span>}{item.source === 'local' && !item.installed ? <Button size="sm" onClick={() => setReview(item)} disabled={installingId === item.id}>{installingId === item.id ? 'Installing…' : 'Review installation'}</Button> : !item.installed ? <span className="text-xs text-muted-foreground">A reviewed server package is required before installation.</span> : null}</div>
      </li>)}</ul> : null}
      <Dialog open={Boolean(review)} onOpenChange={(open) => { if (!open) setReview(null) }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Install {review?.name}</DialogTitle>
            <DialogDescription>This exact package is already present in the reviewed Tessera server image and its manifest hash passed startup validation. Installation records it for your account in a disabled state; enabling and account authorization remain separate actions.</DialogDescription>
          </DialogHeader>
          {review ? <div className="space-y-2 text-sm">
            <p><strong>Publisher:</strong> {review.publisher}</p>
            <p><strong>Version:</strong> {review.version}</p>
            <p><strong>Runtime:</strong> {review.runtime}</p>
            <p><strong>Trust:</strong> {review.trustLevel}</p>
            <p><strong>Sensitivity:</strong> {review.sensitivity}</p>
            <p><strong>Capabilities:</strong> {review.capabilitiesSummary.join(' · ') || 'None declared'}</p>
            <p><strong>Authorization:</strong> {review.authTypes.join(', ') || 'None'}</p>
          </div> : null}
          <DialogFooter>
            <Button variant="outline" onClick={() => setReview(null)}>Cancel</Button>
            <Button onClick={() => { if (review) onInstall(review); setReview(null) }}>Install disabled</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </section>
  )
}