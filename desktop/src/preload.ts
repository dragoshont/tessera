import { contextBridge, ipcRenderer } from 'electron'

const bridge = {
  platform: 'desktop' as const,
  version: '0.1.0',
  getApiOrigin: () => ipcRenderer.invoke('runtime:get-api-origin'),
  loadAuth: () => ipcRenderer.invoke('auth:load'),
  saveAuth: (value: unknown) => ipcRenderer.invoke('auth:save', value),
  signInOidc: (config: unknown) => ipcRenderer.invoke('auth:oidc', config),
  openExternal: (url: string) => ipcRenderer.invoke('runtime:open-external', url),
  notify: (input: unknown) => ipcRenderer.invoke('runtime:notify', input),
  getMacHostStatus: () => ipcRenderer.invoke('host:get-status'),
  setMacHostEnabled: (enabled: boolean) => ipcRenderer.invoke('host:set-enabled', enabled),
  onNavigate: (listener: (route: string) => void) => {
    const handler = (_event: Electron.IpcRendererEvent, route: string) => listener(route)
    ipcRenderer.on('runtime:navigate', handler)
    return () => ipcRenderer.removeListener('runtime:navigate', handler)
  },
}

contextBridge.exposeInMainWorld('tesseraDesktop', Object.freeze(bridge))
