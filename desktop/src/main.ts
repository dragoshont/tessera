import {
  app,
  BrowserWindow,
  globalShortcut,
  ipcMain,
  Menu,
  net,
  Notification,
  protocol,
  session,
  shell,
  type IpcMainInvokeEvent,
} from 'electron'
import { mkdir, readFile, rename, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { registerAppProtocol } from './app-protocol'
import { AuthStore } from './auth-store'
import { OidcCoordinator } from './oidc'
import {
  API_ORIGIN,
  APP_URL,
  assertRendererUrl,
  parseDeepLink,
  validateAuthState,
  validateExternalUrl,
  validateNotification,
  validateOidcInput,
  validateRoute,
} from './security'

protocol.registerSchemesAsPrivileged([
  {
    scheme: 'app',
    privileges: {
      standard: true,
      secure: true,
      supportFetchAPI: true,
      corsEnabled: true,
      stream: true,
    },
  },
])

const testUserData = process.env.TESSERA_ELECTRON_TEST_USER_DATA
const smokeMarker = process.env.TESSERA_ELECTRON_SMOKE_MARKER
if (testUserData) app.setPath('userData', testUserData)
if (!app.requestSingleInstanceLock()) app.quit()

let mainWindow: BrowserWindow | null = null
let queuedDeepLinks: string[] = []
let oidc: OidcCoordinator
let authStore: AuthStore

app.on('open-url', (event, url) => {
  event.preventDefault()
  void receiveDeepLink(url)
})
app.on('second-instance', (_event, argv) => {
  for (const value of argv) if (value.startsWith('tessera://')) void receiveDeepLink(value)
  focusWindow()
})

void app.whenReady().then(async () => {
  app.setName('Tessera')
  if (!testUserData) app.setAsDefaultProtocolClient('tessera')
  authStore = new AuthStore()
  oidc = new OidcCoordinator(authStore)
  registerAppProtocol(path.join(__dirname, '..', 'renderer'))
  registerIpc()
  installPermissionPolicy()
  mainWindow = await createWindow()
  if (testUserData && smokeMarker) {
    await writeFile(smokeMarker, 'ready', { mode: 0o600 })
    app.quit()
    return
  }
  installMenu()
  registerShortcut()
  for (const link of process.argv.filter((value) => value.startsWith('tessera://'))) queuedDeepLinks.push(link)
  for (const link of queuedDeepLinks.splice(0)) await receiveDeepLink(link)
})

app.on('activate', () => {
  if (BrowserWindow.getAllWindows().length === 0) void createWindow().then((window) => { mainWindow = window })
  else focusWindow()
})
app.on('window-all-closed', () => app.quit())
app.on('will-quit', () => globalShortcut.unregisterAll())

async function createWindow(): Promise<BrowserWindow> {
  const state = await loadWindowState()
  const window = new BrowserWindow({
    title: 'Tessera',
    width: state.width,
    height: state.height,
    x: state.x,
    y: state.y,
    minWidth: 920,
    minHeight: 640,
    show: false,
    backgroundColor: '#111318',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      nodeIntegration: false,
      contextIsolation: true,
      sandbox: true,
      webSecurity: true,
      webviewTag: false,
      spellcheck: true,
      devTools: !app.isPackaged,
    },
  })
  window.once('ready-to-show', () => window.show())
  window.on('close', () => void saveWindowState(window))
  window.webContents.setWindowOpenHandler(({ url }) => {
    void openExternal(url)
    return { action: 'deny' }
  })
  window.webContents.on('will-navigate', (event, url) => {
    if (url.startsWith(APP_URL)) return
    event.preventDefault()
    void openExternal(url)
  })
  window.webContents.on('will-attach-webview', (event) => event.preventDefault())
  window.webContents.session.on('will-download', (event) => event.preventDefault())
  await window.loadURL(APP_URL)
  return window
}

function registerIpc(): void {
  ipcMain.handle('runtime:get-api-origin', (event) => {
    assertSender(event)
    return API_ORIGIN
  })
  ipcMain.handle('auth:load', async (event) => {
    assertSender(event)
    return (await authStore.load()).auth
  })
  ipcMain.handle('auth:save', async (event, value: unknown) => {
    assertSender(event)
    const auth = validateAuthState(value, false)
    await authStore.saveAuth(auth)
  })
  ipcMain.handle('auth:oidc', async (event, value: unknown) => {
    assertSender(event)
    return oidc.start(validateOidcInput(value))
  })
  ipcMain.handle('runtime:open-external', async (event, value: unknown) => {
    assertSender(event)
    await openExternal(value)
  })
  ipcMain.handle('runtime:notify', (event, value: unknown) => {
    assertSender(event)
    const input = validateNotification(value)
    const notification = new Notification({ title: input.title, body: input.body })
    if (input.route) notification.on('click', () => navigate(input.route!))
    notification.show()
  })
}

function assertSender(event: IpcMainInvokeEvent): void {
  if (!mainWindow || event.sender !== mainWindow.webContents || event.senderFrame !== mainWindow.webContents.mainFrame)
    throw new Error('IPC sender is not the main Tessera frame.')
  assertRendererUrl(event.senderFrame.url)
}

function installPermissionPolicy(): void {
  session.defaultSession.setPermissionCheckHandler(() => false)
  session.defaultSession.setPermissionRequestHandler((_contents, _permission, callback) => callback(false))
}

async function openExternal(value: unknown): Promise<void> {
  await shell.openExternal(validateExternalUrl(value))
}

async function receiveDeepLink(value: string): Promise<void> {
  if (!oidc) {
    queuedDeepLinks.push(value)
    return
  }
  try {
    const link = parseDeepLink(value)
    if (link.kind === 'auth') {
      await oidc.callback(link.url)
      navigate('/chat')
    } else navigate(link.route)
  } catch {
    navigate('/sign-in')
  }
}

function navigate(route: string): void {
  const value = validateRoute(route)
  focusWindow()
  mainWindow?.webContents.send('runtime:navigate', value)
}

function focusWindow(): void {
  if (!mainWindow) return
  if (mainWindow.isMinimized()) mainWindow.restore()
  mainWindow.show()
  mainWindow.focus()
}

function registerShortcut(): void {
  if (!globalShortcut.register('Alt+Space', focusWindow))
    globalShortcut.register('CommandOrControl+Shift+Space', focusWindow)
}

function installMenu(): void {
  Menu.setApplicationMenu(Menu.buildFromTemplate([
    {
      label: 'Tessera',
      submenu: [
        { role: 'about' },
        { type: 'separator' },
        { label: 'Settings', accelerator: 'CommandOrControl+,', click: () => navigate('/settings') },
        { type: 'separator' },
        { role: 'hide' },
        { role: 'hideOthers' },
        { role: 'unhide' },
        { type: 'separator' },
        { role: 'quit' },
      ],
    },
    {
      label: 'Navigate',
      submenu: [
        { label: 'Chat', accelerator: 'CommandOrControl+1', click: () => navigate('/chat') },
        { label: 'Jobs', accelerator: 'CommandOrControl+2', click: () => navigate('/jobs') },
        { label: 'Accounts', accelerator: 'CommandOrControl+3', click: () => navigate('/accounts') },
        { label: 'Activity', accelerator: 'CommandOrControl+4', click: () => navigate('/activity') },
      ],
    },
    { label: 'Edit', submenu: [{ role: 'undo' }, { role: 'redo' }, { type: 'separator' }, { role: 'cut' }, { role: 'copy' }, { role: 'paste' }, { role: 'selectAll' }] },
    { label: 'Window', submenu: [{ role: 'minimize' }, { role: 'zoom' }, { role: 'front' }] },
  ]))
}

interface WindowState { width: number; height: number; x?: number; y?: number }
const DEFAULT_WINDOW: WindowState = { width: 1280, height: 860 }

async function loadWindowState(): Promise<WindowState> {
  try {
    const raw = await readFile(path.join(app.getPath('userData'), 'window.json'), 'utf8')
    const value = JSON.parse(raw) as WindowState
    if (value.width < 920 || value.width > 4096 || value.height < 640 || value.height > 2160) return DEFAULT_WINDOW
    return value
  } catch { return DEFAULT_WINDOW }
}

async function saveWindowState(window: BrowserWindow): Promise<void> {
  const bounds = window.getBounds()
  const directory = app.getPath('userData')
  await mkdir(directory, { recursive: true, mode: 0o700 })
  const target = path.join(directory, 'window.json')
  const temporary = `${target}.${process.pid}.tmp`
  await writeFile(temporary, JSON.stringify(bounds), { mode: 0o600 })
  await rename(temporary, target)
}
