import { useState } from 'react'
import type { R2IntegrationCatalogItem, R2IntegrationSource } from '../../api/r2'
import { Alert, AlertDescription } from '../ui/alert'
import { Badge } from '../ui/badge'
import { Button } from '../ui/button'
import { Input } from '../ui/input'
import { ProductStateBadge } from './R2ProductComponents'

export function IntegrationSearchPanel({ query, items, sources, loading, errorMessage, onSearch, onInspect }: {
  query: string
  items: R2IntegrationCatalogItem[]
  sources: R2IntegrationSource[]
  loading: boolean
  errorMessage: string | null
  onSearch: (query: string) => void
  onInspect: (url: string) => void
}) {
  const [searchText, setSearchText] = useState(query)
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
        <div className="mt-3 flex flex-wrap items-center gap-2">{item.inspectUrl ? <Button size="sm" variant="outline" onClick={() => onInspect(item.inspectUrl!)}>Inspect source</Button> : <span className="text-xs text-muted-foreground">Built into the reviewed Tessera server image.</span>}{!item.installed ? <span className="text-xs text-muted-foreground">Review required before server installation.</span> : null}</div>
      </li>)}</ul> : null}
    </section>
  )
}