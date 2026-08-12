import { AlertCircle, Bot, CircleStop, History, Loader2, RotateCcw, Send, Settings2, UserRound } from 'lucide-react'
import { useState } from 'react'
import { Alert, AlertDescription } from '../ui/alert'
import { Badge } from '../ui/badge'
import { Button } from '../ui/button'

export type ChatTurn = { id: string; role: 'user' | 'assistant' | 'event'; text: string; status?: string; retryable?: boolean }

export function ChatWorkspace({
  title = 'New conversation', turns = [], loading = false, errorMessage, configurationRequired = false,
  sending = false, onSend, onConfigure, onStop, onRetry,
}: {
  title?: string; turns?: ChatTurn[]; loading?: boolean; errorMessage?: string; configurationRequired?: boolean
  sending?: boolean; onSend?: (text: string) => void; onConfigure?: () => void; onStop?: () => void; onRetry?: (messageId: string) => void
}) {
  const [text, setText] = useState('')
  const submit = () => { const value = text.trim(); if (!value || sending) return; onSend?.(value); setText('') }
  return (
    <section className="flex min-h-[calc(100vh-8rem)] flex-col" aria-labelledby="chat-title">
      <header className="flex items-center justify-between gap-4 border-b border-border pb-4">
        <div><h1 id="chat-title" className="text-xl font-semibold">{title}</h1><p className="mt-1 text-sm text-muted-foreground">Messages and execution history persist across restarts.</p></div>
        <Badge variant="outline">Durable</Badge>
      </header>
      <div className="flex-1 py-6" aria-live="polite">
        {loading ? <div className="flex items-center gap-2 text-sm text-muted-foreground"><Loader2 className="h-4 w-4 animate-spin" aria-hidden />Loading conversation…</div> : null}
        {errorMessage ? <Alert variant="destructive"><AlertCircle className="h-4 w-4" /><AlertDescription>{errorMessage}</AlertDescription></Alert> : null}
        {configurationRequired ? (
          <div className="mx-auto max-w-md py-16 text-center"><Settings2 className="mx-auto h-9 w-9 text-muted-foreground" aria-hidden /><h2 className="mt-4 text-lg font-semibold">Model configuration required</h2><p className="mt-2 text-sm text-muted-foreground">Connect and validate an OpenAI-compatible model account before sending a message.</p><Button className="mt-5" onClick={onConfigure}><Settings2 aria-hidden />Open settings</Button></div>
        ) : turns.length === 0 && !loading && !errorMessage ? (
          <div className="mx-auto max-w-md py-16 text-center"><Bot className="mx-auto h-9 w-9 text-muted-foreground" aria-hidden /><h2 className="mt-4 text-lg font-semibold">What should Tessera help with?</h2><p className="mt-2 text-sm text-muted-foreground">Ask a question, inspect connected information, or explicitly ask Tessera to remember something.</p></div>
        ) : (
          <ol className="space-y-5" aria-label="Conversation messages">{turns.map((turn) => <li key={turn.id} className="grid grid-cols-[2rem_1fr] gap-3"><span className="flex h-8 w-8 items-center justify-center rounded-full border border-border bg-card">{turn.role === 'user' ? <UserRound className="h-4 w-4" aria-hidden /> : turn.role === 'event' ? <History className="h-4 w-4" aria-hidden /> : <Bot className="h-4 w-4" aria-hidden />}</span><div><div className="flex items-center gap-2 text-xs text-muted-foreground"><span>{turn.role === 'user' ? 'You' : turn.role === 'event' ? 'System event' : 'Tessera'}</span>{turn.status ? <Badge variant="outline">{turn.status}</Badge> : null}</div><p className="mt-1 whitespace-pre-wrap text-sm leading-6">{turn.text}</p>{turn.retryable ? <Button className="mt-2" size="sm" variant="outline" onClick={() => onRetry?.(turn.id)}><RotateCcw aria-hidden />Retry</Button> : null}</div></li>)}</ol>
        )}
      </div>
      {!configurationRequired ? <form className="sticky bottom-0 border-t border-border bg-surface py-4" onSubmit={(event) => { event.preventDefault(); submit() }}><label htmlFor="chat-message" className="sr-only">Message Tessera</label><div className="flex items-end gap-2 rounded-lg border border-border bg-card p-2"><textarea id="chat-message" value={text} onChange={(event) => setText(event.target.value)} rows={2} placeholder="Message Tessera" className="min-h-11 flex-1 resize-none bg-transparent px-2 py-2 text-sm outline-none" />{sending ? <Button type="button" variant="outline" onClick={onStop}><CircleStop aria-hidden />Stop</Button> : <Button type="submit" disabled={!text.trim()} aria-label="Send message"><Send aria-hidden /></Button>}</div></form> : null}
    </section>
  )
}