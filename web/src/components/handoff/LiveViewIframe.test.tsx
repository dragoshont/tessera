import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { LiveViewIframe } from './LiveViewIframe'
import { demoLiveViewHandle } from '../../data/fixtures'

function fireWindowMessage(data: unknown, origin: string, source: MessageEventSource | null) {
  window.dispatchEvent(new MessageEvent('message', { data, origin, source }))
}

function iframeWindow() {
  return (screen.getByTitle('Live remote browser') as HTMLIFrameElement).contentWindow
}

describe('LiveViewIframe — worker postMessage contract', () => {
  it('routes a bare-string tessera-session-done to onMessage', () => {
    const onMessage = vi.fn()
    render(<LiveViewIframe handle={demoLiveViewHandle} onMessage={onMessage} />)

    fireWindowMessage('tessera-session-done', window.location.origin, iframeWindow())

    expect(onMessage).toHaveBeenCalledWith('tessera-session-done')
  })

  it('also accepts the { type } object form', () => {
    const onMessage = vi.fn()
    render(<LiveViewIframe handle={demoLiveViewHandle} onMessage={onMessage} />)

    fireWindowMessage({ type: 'tessera-session-expired' }, window.location.origin, iframeWindow())

    expect(onMessage).toHaveBeenCalledWith('tessera-session-expired')
  })

  it('ignores unrelated messages', () => {
    const onMessage = vi.fn()
    render(<LiveViewIframe handle={demoLiveViewHandle} onMessage={onMessage} />)

    fireWindowMessage('some-other-app-event', window.location.origin, iframeWindow())
    fireWindowMessage({ type: 'not-ours' }, window.location.origin, iframeWindow())

    expect(onMessage).not.toHaveBeenCalled()
  })

  it('ignores a valid worker message from another origin', () => {
    const onMessage = vi.fn()
    render(<LiveViewIframe handle={demoLiveViewHandle} onMessage={onMessage} />)

    fireWindowMessage('tessera-session-done', 'https://attacker.example', iframeWindow())

    expect(onMessage).not.toHaveBeenCalled()
  })

  it('ignores a valid worker message from another window', () => {
    const onMessage = vi.fn()
    render(<LiveViewIframe handle={demoLiveViewHandle} onMessage={onMessage} />)

    fireWindowMessage('tessera-session-done', window.location.origin, window)

    expect(onMessage).not.toHaveBeenCalled()
  })

  it('treats the iframe load as a readiness signal (R2 fallback)', () => {
    const onReady = vi.fn()
    render(<LiveViewIframe handle={demoLiveViewHandle} onMessage={() => undefined} onReady={onReady} />)

    fireEvent.load(screen.getByTitle('Live remote browser'))

    expect(onReady).toHaveBeenCalledTimes(1)
  })

  it('never renders the raw worker URL as text', () => {
    render(<LiveViewIframe handle={demoLiveViewHandle} onMessage={() => undefined} />)

    // The URL is the iframe src, never visible copy.
    expect(screen.queryByText(demoLiveViewHandle.liveViewUrl)).toBeNull()
  })
})
