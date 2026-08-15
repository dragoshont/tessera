import type { TesseraHttpClient } from './http'
import type {
  Page,
  RemoteHostArtifactDetailDto,
  RemoteHostArtifactSummaryDto,
  RemoteHostDetailDto,
  RemoteHostPairingDto,
  RemoteHostRunProjectionDto,
  RemoteHostSummaryDto,
} from './types'

export type ConfirmRemoteHostPairingInput = {
  expectedVersion: number
  confirmationCode: string
  displayName: string
  capabilityGrants: Array<{ capabilityId: string; capabilityVersion: string }>
  resourceGrants: Array<{ resourceId: string; accessMode: 'READ_ONLY' }>
}

export function createRemoteApi(http: TesseraHttpClient) {
  return {
    hosts: () => http.request<Page<RemoteHostSummaryDto>>('/hosts'),
    host: (hostId: string) => http.request<RemoteHostDetailDto>(`/hosts/${encodeURIComponent(hostId)}`),
    createPairing: (claimSecretHash: string) => http.mutate<RemoteHostPairingDto>('/host-pairings', 'POST', { claimSecretHash }, 'host-pairing'),
    pairing: (pairingId: string) => http.request<RemoteHostPairingDto>(`/host-pairings/${encodeURIComponent(pairingId)}`),
    confirmPairing: (pairingId: string, input: ConfirmRemoteHostPairingInput) => http.mutate<RemoteHostDetailDto>(`/host-pairings/${encodeURIComponent(pairingId)}/confirm`, 'POST', input, 'host-pairing-confirm'),
    cancelPairing: (pairing: Pick<RemoteHostPairingDto, 'pairingId' | 'version'>) => http.mutate<{ version: number }>(`/host-pairings/${encodeURIComponent(pairing.pairingId)}/cancel`, 'POST', { expectedVersion: pairing.version }, 'host-pairing-cancel'),
    revokeHost: (host: Pick<RemoteHostSummaryDto, 'hostId' | 'version'>) => http.mutate<RemoteHostDetailDto>(`/hosts/${encodeURIComponent(host.hostId)}/revoke`, 'POST', { expectedVersion: host.version }, 'host-revoke'),
    runProjection: (runId: string) => http.request<RemoteHostRunProjectionDto>(`/job-runs/${encodeURIComponent(runId)}/remote`),
    runArtifacts: (runId: string, cursor?: string) => http.request<Page<RemoteHostArtifactSummaryDto>>(`/job-runs/${encodeURIComponent(runId)}/remote-artifacts${cursor ? `?cursor=${encodeURIComponent(cursor)}` : ''}`),
    artifact: (artifactId: string) => http.request<RemoteHostArtifactDetailDto>(`/host-artifacts/${encodeURIComponent(artifactId)}`),
  }
}

export type RemoteApi = ReturnType<typeof createRemoteApi>