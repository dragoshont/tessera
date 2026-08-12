import Constants from 'expo-constants'
import type { RouteCandidate } from '@tessera/client'

type TesseraExtra = { serverId?: string; remoteOrigin?: string; localOrigin?: string; clientVersion?: string }
const extra = (Constants.expoConfig?.extra?.tessera ?? {}) as TesseraExtra
const remoteOrigin = extra.remoteOrigin ?? 'https://tessera.example'
const localOrigin = extra.localOrigin?.trim()

const routes: RouteCandidate[] = []
if (localOrigin && localOrigin !== remoteOrigin) routes.push({ kind: 'LOCAL', origin: localOrigin, timeoutMs: 1_500 })
routes.push({ kind: 'REMOTE', origin: remoteOrigin, timeoutMs: 4_000 })

export const runtimeConfig = {
  expectedServerId: extra.serverId ?? 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
  clientVersion: extra.clientVersion ?? '0.1.0',
  routes,
}