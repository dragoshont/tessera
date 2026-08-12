import { protocol } from 'electron'
import { readFile } from 'node:fs/promises'
import path from 'node:path'

const CSP = [
  "default-src 'none'",
  "script-src 'self'",
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' data:",
  "font-src 'self'",
  "connect-src https://tessera.hont.ro",
  "object-src 'none'",
  "base-uri 'none'",
  "form-action 'none'",
  "frame-src 'none'",
  "frame-ancestors 'none'",
  "worker-src 'none'",
].join('; ')

export function registerAppProtocol(rendererRoot: string): void {
  protocol.handle('app', async (request) => {
    const url = new URL(request.url)
    if (url.host !== 'tessera') return new Response('Not found', { status: 404 })
    const decoded = decodeURIComponent(url.pathname)
    if (decoded.includes('..') || decoded.includes('\\')) return new Response('Not found', { status: 404 })
    const requested = decoded.replace(/^\/+/, '')
    const candidate = requested && path.extname(requested) ? requested : 'index.html'
    const resolved = path.resolve(rendererRoot, candidate)
    if (!resolved.startsWith(`${path.resolve(rendererRoot)}${path.sep}`) && resolved !== path.resolve(rendererRoot, 'index.html'))
      return new Response('Not found', { status: 404 })
    try {
      const body = await readFile(resolved)
      return new Response(body, {
        headers: {
          'Content-Type': contentType(resolved),
          'Content-Security-Policy': CSP,
          'X-Content-Type-Options': 'nosniff',
          'Referrer-Policy': 'no-referrer',
          'Cross-Origin-Opener-Policy': 'same-origin',
          'Cross-Origin-Resource-Policy': 'same-origin',
          'Cache-Control': path.basename(resolved) === 'index.html' ? 'no-store' : 'public, max-age=31536000, immutable',
        },
      })
    } catch {
      return new Response('Not found', { status: 404 })
    }
  })
}

function contentType(file: string): string {
  switch (path.extname(file)) {
    case '.html': return 'text/html; charset=utf-8'
    case '.js': return 'text/javascript; charset=utf-8'
    case '.css': return 'text/css; charset=utf-8'
    case '.svg': return 'image/svg+xml'
    case '.png': return 'image/png'
    case '.woff2': return 'font/woff2'
    default: return 'application/octet-stream'
  }
}
