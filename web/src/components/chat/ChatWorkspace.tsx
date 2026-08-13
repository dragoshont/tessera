import { AlertCircle, Bot, CircleStop, Headphones, History, Loader2, Mic, MicOff, PhoneOff, RotateCcw, Send, Settings2, ShieldAlert, UserRound, Volume2 } from 'lucide-react'
import { useState } from 'react'
import { Alert, AlertDescription } from '../ui/alert'
import { Badge } from '../ui/badge'
import { Button } from '../ui/button'

export type ChatTurn = { id: string; role: 'user' | 'assistant' | 'event'; text: string; status?: string; retryable?: boolean }
export type RealtimeVoiceState = 'UNAVAILABLE' | 'IDLE' | 'REQUESTING_PERMISSION' | 'PERMISSION_DENIED' | 'NEGOTIATING' | 'LISTENING' | 'USER_SPEAKING' | 'ASSISTANT_SPEAKING' | 'TOOL_RUNNING' | 'APPROVAL_REQUIRED' | 'INTERRUPTED' | 'SESSION_EXPIRED' | 'AUDIO_OUTPUT_BLOCKED' | 'ERROR' | 'ENDING'
export type RealtimeVoiceView = { state: RealtimeVoiceState; muted?: boolean; blockedCode?: string | null; userCaption?: string; assistantCaption?: string; toolName?: string }

const VOICE_LABELS: Record<RealtimeVoiceState, string> = {
  UNAVAILABLE: 'Voice unavailable', IDLE: 'Voice ready', REQUESTING_PERMISSION: 'Requesting microphone permission',
  PERMISSION_DENIED: 'Microphone permission denied', NEGOTIATING: 'Negotiating secure voice session', LISTENING: 'Listening',
  USER_SPEAKING: 'You are speaking', ASSISTANT_SPEAKING: 'Tessera is speaking', TOOL_RUNNING: 'Tool running',
  APPROVAL_REQUIRED: 'Approval required', INTERRUPTED: 'Voice interrupted', SESSION_EXPIRED: 'Voice session expired',
  AUDIO_OUTPUT_BLOCKED: 'Audio playback blocked', ERROR: 'Voice error', ENDING: 'Ending voice session',
}

export function RealtimeVoiceControl({ voice, onStart, onRetry, onToggleMute, onInterrupt, onEnd, onEnableAudio }: {
  voice: RealtimeVoiceView; onStart?: () => void; onRetry?: () => void; onToggleMute?: () => void; onInterrupt?: () => void; onEnd?: () => void; onEnableAudio?: () => void
}) {
  const active = ['NEGOTIATING','LISTENING','USER_SPEAKING','ASSISTANT_SPEAKING','TOOL_RUNNING','APPROVAL_REQUIRED','ENDING'].includes(voice.state)
  const pending = voice.state === 'REQUESTING_PERMISSION' || voice.state === 'NEGOTIATING' || voice.state === 'ENDING'
  const retryable = ['PERMISSION_DENIED','INTERRUPTED','SESSION_EXPIRED','ERROR'].includes(voice.state)
  const detail = voice.state === 'UNAVAILABLE' ? (voice.blockedCode ?? 'The server has not enabled realtime voice.')
    : voice.state === 'PERMISSION_DENIED' ? 'Allow microphone access in browser or system settings, then retry explicitly.'
    : voice.state === 'APPROVAL_REQUIRED' ? 'Voice cannot approve consequential actions. Review the exact Action in Chat.'
    : voice.state === 'SESSION_EXPIRED' ? 'The microphone and peer connection are closed. Start a new session to continue.'
    : voice.state === 'AUDIO_OUTPUT_BLOCKED' ? 'Your browser requires a gesture before remote audio can play.'
    : voice.state === 'INTERRUPTED' ? 'Capture stopped after a network or audio interruption. Reconnect explicitly.'
    : voice.state === 'ERROR' ? 'Voice ended safely. Typed Chat and saved transcripts remain available.'
    : voice.state === 'TOOL_RUNNING' ? `Running ${voice.toolName ?? 'a reviewed tool'} through Tessera.`
    : voice.state === 'NEGOTIATING' ? 'Tessera is exchanging SDP only. Audio goes directly between this device and Foundry.'
    : voice.muted ? 'Microphone muted. The session remains connected.'
    : 'Captions remain visible and completed turns are saved to this conversation.'
  return (
    <section className="border-b border-border py-4" aria-labelledby="voice-title">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2"><Headphones className="h-4 w-4" aria-hidden /><h2 id="voice-title" className="text-sm font-semibold">Realtime voice</h2></div>
          <p className="mt-1 text-xs text-muted-foreground">Direct WebRTC media · completed text saved here</p>
        </div>
        <Badge variant="outline" className="gap-1.5"><span className={voice.state === 'USER_SPEAKING' || voice.state === 'ASSISTANT_SPEAKING' ? 'h-2 w-2 rounded-full bg-health-live' : 'h-2 w-2 rounded-full bg-muted-foreground'} aria-hidden />{VOICE_LABELS[voice.state]}</Badge>
      </div>
      <p className="mt-3 text-sm text-muted-foreground" role="status" aria-live="polite">{detail}</p>
      {voice.userCaption || voice.assistantCaption ? <div className="mt-3 grid gap-2 border-y border-border py-3 text-sm" aria-label="Voice captions" aria-live="off">
        {voice.userCaption ? <p><span className="font-medium">You:</span> {voice.userCaption}</p> : null}
        {voice.assistantCaption ? <p><span className="font-medium">Tessera:</span> {voice.assistantCaption}</p> : null}
      </div> : null}
      <div className="mt-3 flex flex-wrap gap-2">
        {voice.state === 'IDLE' ? <Button type="button" className="min-h-11" onClick={onStart}><Mic aria-hidden />Start voice</Button> : null}
        {voice.state === 'UNAVAILABLE' ? <Button type="button" className="min-h-11" disabled><MicOff aria-hidden />Voice unavailable</Button> : null}
        {pending ? <Button type="button" className="min-h-11" disabled><Loader2 className="animate-spin motion-reduce:animate-none" aria-hidden />{voice.state === 'ENDING' ? 'Ending…' : 'Connecting…'}</Button> : null}
        {retryable ? <Button type="button" className="min-h-11" onClick={onRetry}><RotateCcw aria-hidden />Retry voice</Button> : null}
        {voice.state === 'AUDIO_OUTPUT_BLOCKED' ? <Button type="button" className="min-h-11" onClick={onEnableAudio}><Volume2 aria-hidden />Enable audio</Button> : null}
        {active && voice.state !== 'NEGOTIATING' && voice.state !== 'ENDING' ? <Button type="button" className="min-h-11" variant="outline" aria-pressed={Boolean(voice.muted)} onClick={onToggleMute}>{voice.muted ? <Mic aria-hidden /> : <MicOff aria-hidden />}{voice.muted ? 'Unmute' : 'Mute'}</Button> : null}
        {voice.state === 'ASSISTANT_SPEAKING' ? <Button type="button" className="min-h-11" variant="outline" onClick={onInterrupt}><CircleStop aria-hidden />Interrupt</Button> : null}
        {active && voice.state !== 'ENDING' ? <Button type="button" className="min-h-11" variant="outline" onClick={onEnd}><PhoneOff aria-hidden />End voice</Button> : null}
        {voice.state === 'APPROVAL_REQUIRED' ? <span className="inline-flex min-h-11 items-center gap-2 text-sm font-medium text-health-expiring"><ShieldAlert className="h-4 w-4" aria-hidden />Review Action below</span> : null}
      </div>
    </section>
  )
}

export function ChatWorkspace({
  title = 'New conversation', turns = [], loading = false, errorMessage, configurationRequired = false,
  sending = false, voice, onSend, onConfigure, onStop, onRetry, onVoiceStart, onVoiceRetry, onVoiceToggleMute, onVoiceInterrupt, onVoiceEnd, onVoiceEnableAudio,
}: {
  title?: string; turns?: ChatTurn[]; loading?: boolean; errorMessage?: string; configurationRequired?: boolean
  sending?: boolean; onSend?: (text: string) => void; onConfigure?: () => void; onStop?: () => void; onRetry?: (messageId: string) => void
  voice?: RealtimeVoiceView; onVoiceStart?: () => void; onVoiceRetry?: () => void; onVoiceToggleMute?: () => void; onVoiceInterrupt?: () => void; onVoiceEnd?: () => void; onVoiceEnableAudio?: () => void
}) {
  const [text, setText] = useState('')
  const submit = () => { const value = text.trim(); if (!value || sending) return; onSend?.(value); setText('') }
  return (
    <section className="flex min-h-[calc(100vh-8rem)] flex-col" aria-labelledby="chat-title">
      <header className="flex items-center justify-between gap-4 border-b border-border pb-4">
        <div><h1 id="chat-title" className="text-xl font-semibold">{title}</h1><p className="mt-1 text-sm text-muted-foreground">Messages and execution history persist across restarts.</p></div>
        <Badge variant="outline">Durable</Badge>
      </header>
      {voice ? <RealtimeVoiceControl voice={voice} onStart={onVoiceStart} onRetry={onVoiceRetry} onToggleMute={onVoiceToggleMute} onInterrupt={onVoiceInterrupt} onEnd={onVoiceEnd} onEnableAudio={onVoiceEnableAudio} /> : null}
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