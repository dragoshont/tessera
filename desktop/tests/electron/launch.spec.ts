import { _electron as electron, expect, test, type ElectronApplication } from '@playwright/test'
import { mkdtemp, rm } from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'

const root = path.resolve(__dirname, '../..')
const packaged = process.env.TESSERA_PACKAGED_APP

test('real Electron shell launches with hardened renderer and narrow bridge', async () => {
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
      'loadAuth',
      'notify',
      'onNavigate',
      'openExternal',
      'platform',
      'saveAuth',
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
  } finally {
    await application?.close()
    await rm(userData, { recursive: true, force: true })
  }
})
