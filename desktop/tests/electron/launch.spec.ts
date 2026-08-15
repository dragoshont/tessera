import { _electron as electron, expect, test, type ElectronApplication } from '@playwright/test'
import { mkdtemp, rm } from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'

const root = path.resolve(__dirname, '../..')
const packaged = process.env.TESSERA_PACKAGED_APP

test('real Electron shell launches with hardened renderer and narrow bridge', async () => {
  test.setTimeout(120_000)
  const userData = await mkdtemp(path.join(os.tmpdir(), 'tessera-electron-test-'))
  let application: ElectronApplication | undefined
  try {
    const launchOptions = {
      env: { ...process.env, TESSERA_ELECTRON_TEST_USER_DATA: userData },
    }
    application = await electron.launch(
      packaged
        ? { ...launchOptions, executablePath: packaged }
        : { ...launchOptions, args: [root], cwd: root },
    )
    const window = await application.firstWindow()
    await expect(window).toHaveTitle('Tessera')
    const renderer = await window.evaluate(() => ({
      node: typeof (globalThis as { require?: unknown }).require,
      process: typeof (globalThis as { process?: unknown }).process,
      bridge: Object.keys(window.tesseraDesktop ?? {}).sort(),
      origin: location.href,
    }))
    expect(renderer.node).toBe('undefined')
    expect(renderer.process).toBe('undefined')
    expect(renderer.origin).toBe('app://tessera/')
    expect(renderer.bridge).toEqual([
      'getApiOrigin',
      'getMacHostStatus',
      'loadAuth',
      'notify',
      'onNavigate',
      'openExternal',
      'platform',
      'saveAuth',
      'setMacHostEnabled',
      'signInOidc',
      'version',
    ].sort())
    const preferences = await application.evaluate(({ BrowserWindow }) => {
      const webContents = BrowserWindow.getAllWindows()[0]?.webContents
      return webContents?.getLastWebPreferences()
    })
    expect(preferences).toMatchObject({
      nodeIntegration: false,
      contextIsolation: true,
      sandbox: true,
      webSecurity: true,
      webviewTag: false,
    })
    await window.route('https://tessera.hont.ro/**', async (route) => {
      const url = new URL(route.request().url())
      const host = {
        hostId: 'host-main', displayName: 'Home Mac', platform: 'macOS', architecture: 'arm64', lifecycle: 'ONLINE', connectionStatus: 'CONNECTED',
        agentVersion: '1.0.0', protocolVersion: '1', lastSeenAt: '2026-08-14T12:00:00Z', pairedAt: '2026-08-14T11:00:00Z', revokedAt: null, version: 1,
      }
      let body: unknown
      if (url.pathname === '/portal/config') body = { authMode: 'dev', devLoopback: true }
      else if (url.pathname === '/portal/me') body = { principal: 'alice@example.com', role: 'Member', connectionCount: 0, needsAttentionCount: 0 }
      else if (url.pathname === '/api/v1/hosts') body = { items: [host], nextCursor: null }
      else if (url.pathname === '/api/v1/hosts/host-main') body = { host, capabilities: [], capabilityGrants: [], resources: [], resourceGrants: [] }
      else if (url.pathname === '/api/v1/jobs' || url.pathname === '/api/v1/actions' || url.pathname === '/api/v1/accounts') body = { items: [], nextCursor: null }
      else body = { code: 'not_found' }
      await route.fulfill({ status: 'code' in (body as Record<string, unknown>) ? 404 : 200, contentType: 'application/json', body: JSON.stringify(body) })
    })
    await window.evaluate(async () => {
      await window.tesseraDesktop!.saveAuth({ kind: 'dev', principal: 'alice@example.com' })
    })
    await window.goto('app://tessera/remote')
    await expect(window.getByRole('heading', { name: 'Remote Host preview' })).toBeVisible()
    await expect(window.getByRole('heading', { name: 'Mac Host mode' })).toBeVisible()
    await expect(window.getByRole('link', { name: 'Home Mac' }).first()).toBeVisible()
    await expect(window.getByText(/native keys and repository paths remain helper-owned/i)).toBeVisible()
    await window.screenshot({ path: path.resolve(root, '../.architrave/runs/20260813-remote-hosts-final/legibility/remote-electron.png'), fullPage: true })
  } finally {
    await application?.close()
    await rm(userData, { recursive: true, force: true })
  }
})
