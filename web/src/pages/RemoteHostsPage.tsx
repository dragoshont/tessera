import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { RefreshCw } from 'lucide-react'
import { useNavigate, useParams } from 'react-router-dom'
import type {
  RemoteHostArtifactSummaryDto,
} from '@tessera/client'
import { TesseraProblem } from '@tessera/client'
import { remoteApi, r2Api } from '../api/r2'
import { getMacHostStatus, isDesktop, setMacHostEnabled } from '../app/runtime'
import {
  RemoteHostDetail,
  RemoteWorkspace,
  type RemoteArtifact,
  type RemoteHostDetailState,
  type RemoteHostSummary,
} from '../components/remote/RemoteWorkspace'
import { ActionApprovalCard } from '../components/product/R2ProductComponents'
import { MacHostRolePanel } from '../components/remote/MacHostRolePanel'
import { Alert, AlertDescription, AlertTitle } from '../components/ui/alert'
import { Skeleton } from '../components/ui/skeleton'
import { loadRemoteInventory, type RemoteInventoryRecord } from '../lib/remote-inventory'

function pairingUnavailableReason(): string {
  return 'Pairing remains unavailable in this preview until the signed Mac helper journey is verified.'
}

function DesktopHostMode() {
  const client = useQueryClient()
  const status = useQuery({ queryKey: ['remote', 'mac-host-status'], queryFn: getMacHostStatus, enabled: isDesktop() })
  const update = useMutation({
    mutationFn: setMacHostEnabled,
    onSuccess: (value) => client.setQueryData(['remote', 'mac-host-status'], value),
  })
  if (!isDesktop()) return null
  const value = status.data
  return <MacHostRolePanel status={value} checking={status.isLoading} busy={update.isPending} error={update.error instanceof Error ? update.error.message : null} onSetEnabled={(next) => update.mutate(next)} />
}

export function RemoteHostsPage() {
  const navigate = useNavigate()
  const client = useQueryClient()
  const query = useQuery({ queryKey: ['remote', 'inventory'], queryFn: () => loadRemoteInventory(remoteApi, r2Api), refetchInterval: 15_000 })
  const revoke = useMutation({
    mutationFn: (host: RemoteHostSummary) => remoteApi.revokeHost({ hostId: host.hostId, version: host.version }),
    onError: (error) => {
      if (error instanceof TesseraProblem && error.status === 409) void query.refetch()
    },
    onSuccess: () => void client.invalidateQueries({ queryKey: ['remote'] }),
  })
  const unsupported = query.error instanceof TesseraProblem && [404, 405, 501].includes(query.error.status)
  const mode = query.isLoading ? 'loading'
    : unsupported ? 'unsupported'
      : query.error || query.data?.partial ? 'partial-error'
        : query.data?.records.length ? 'populated'
          : 'zero-hosts'
  return (
    <div className="space-y-5">
      <DesktopHostMode />
      <RemoteWorkspace
        mode={mode}
        hosts={query.data?.records.map((item) => item.host) ?? []}
        partialErrorMessage={query.error instanceof Error ? query.error.message : undefined}
        lastSuccessfulStatusAt={query.data?.loadedAt}
        announcement={revoke.isSuccess ? 'Host revoked. Historical work remains available.' : undefined}
        pairingUnavailableReason={pairingUnavailableReason()}
        onRetry={() => void query.refetch()}
        onOpenHost={(hostId) => navigate(`/remote/hosts/${encodeURIComponent(hostId)}`)}
        onRevokeHost={(host) => revoke.mutate(host)}
      />
    </div>
  )
}

function mapArtifact(artifact: RemoteHostArtifactSummaryDto, textContent?: string | null): RemoteArtifact {
  return {
    artifactId: artifact.artifactId,
    summary: artifact.summary,
    kind: artifact.kind,
    mediaType: artifact.mediaType,
    sizeBytes: artifact.sizeBytes,
    sha256: artifact.sha256,
    retention: artifact.retention,
    createdAt: artifact.createdAt,
    expiresAt: artifact.expiresAt,
    redacted: artifact.redacted,
    truncated: artifact.truncated,
    contentState: artifact.contentState,
    ...(textContent !== undefined ? { textContent } : {}),
  }
}

function detailState(record: RemoteInventoryRecord): RemoteHostDetailState {
  if (record.source.lifecycle === 'REVOKED') return 'revoked'
  if (record.source.lifecycle === 'UPDATE_REQUIRED') return 'update-required'
  if (record.source.lifecycle === 'OFFLINE') return 'offline-waiting-for-host'
  if (record.pendingActions.some((item) => item.state === 'PROPOSED')) return 'approval-required'
  if (record.job?.lastRun?.state === 'SUCCEEDED' && record.projection?.artifacts.some((item) => item.contentState === 'EXPIRED')) return 'expired-artifact'
  if (record.job?.lastRun?.state === 'SUCCEEDED' && record.projection?.artifacts.some((item) => item.truncated)) return 'truncated-artifact'
  if (record.job?.lastRun?.state === 'SUCCEEDED') return 'succeeded-with-artifacts'
  return record.job ? 'busy-running' : 'online-idle'
}

function blockerText(record: RemoteInventoryRecord): string | null {
  const blocker = record.projection?.blocker
  if (!blocker) return null
  switch (blocker.code) {
  case 'WAITING_FOR_HOST': case 'HOST_DISCONNECTED': return `Waiting for ${record.host.displayName} to reconnect. The Job remains durable.`
  case 'WAITING_FOR_CAPABILITY': return `Waiting for capability ${blocker.capabilityId ?? 'access'} on ${record.host.displayName}.`
  case 'WAITING_FOR_RESOURCE': return `Waiting for resource ${blocker.resourceId ?? 'access'} on ${record.host.displayName}.`
  case 'HOST_UPDATE_REQUIRED': return `${record.host.displayName} must be updated before this Job can continue.`
  default: return blocker.detailCode ? blocker.detailCode.replaceAll('_', ' ').toLowerCase() : blocker.code.replaceAll('_', ' ').toLowerCase()
  }
}

export function RemoteHostPage() {
  const { hostId = '' } = useParams<{ hostId: string }>()
  const client = useQueryClient()
  const query = useQuery({ queryKey: ['remote', 'inventory'], queryFn: () => loadRemoteInventory(remoteApi, r2Api), refetchInterval: 10_000 })
  const record = query.data?.records.find((item) => item.source.hostId === hostId)
  const firstAction = record?.pendingActions.find((item) => item.state === 'PROPOSED')
  const invalidate = () => void client.invalidateQueries({ queryKey: ['remote'] })
  const jobMutation = useMutation({
    mutationFn: (operation: 'pause' | 'cancel') => {
      if (!record?.job) throw new Error('Job is no longer available.')
      return operation === 'pause' ? r2Api.setJobState(record.job, 'pause') : r2Api.cancelJob(record.job)
    },
    onSuccess: invalidate,
  })
  const revoke = useMutation({
    mutationFn: (host: RemoteHostSummary) => remoteApi.revokeHost({ hostId: host.hostId, version: host.version }),
    onSuccess: invalidate,
    onError: (error) => {
      if (error instanceof TesseraProblem && error.status === 409) void query.refetch()
    },
  })
  const action = useMutation({
    mutationFn: (operation: 'approve' | 'cancel') => {
      if (!firstAction) throw new Error('Action is no longer available.')
      return operation === 'approve' ? r2Api.approveAction(firstAction) : r2Api.cancelAction(firstAction)
    },
    onSuccess: invalidate,
  })
  if (query.isLoading) return <div aria-busy="true" className="space-y-3"><Skeleton className="h-10 w-64" /><Skeleton className="h-96 w-full" /></div>
  if (!record) return <Alert variant="destructive"><RefreshCw aria-hidden /><AlertTitle>Remote Host unavailable</AlertTitle><AlertDescription>{query.error instanceof Error ? query.error.message : 'This Host was not found.'}</AlertDescription></Alert>
  const detail = record.detail
  const capabilities = detail?.capabilityGrants.filter((item) => item.revokedAt === null).map((grant) => {
    const advertised = detail.capabilities.find((item) => item.capabilityId === grant.capabilityId && item.capabilityVersion === grant.capabilityVersion)
    return { id: `${grant.capabilityId}@${grant.capabilityVersion}`, label: grant.capabilityId, detail: `${advertised?.sideEffectClass ?? 'Unknown effect'} · version ${grant.capabilityVersion}` }
  }) ?? []
  const resources = detail?.resourceGrants.filter((item) => item.revokedAt === null).map((grant) => {
    const advertised = detail.resources.find((item) => item.resourceId === grant.resourceId)
    return { id: grant.resourceId, label: advertised?.displayName ?? grant.resourceId, detail: `${advertised?.type ?? 'Resource'} · ${grant.accessMode}` }
  }) ?? []
  return (
    <RemoteHostDetail
      state={detailState(record)}
      host={record.host}
      blocker={blockerText(record)}
      checkpoints={record.projection?.checkpoints.map((item) => ({ sequence: item.sequence, step: item.step, summary: item.step.replaceAll('_', ' ').toLowerCase(), occurredAt: item.createdAt })) ?? []}
      artifacts={record.projection?.artifacts.map((item) => mapArtifact(item)) ?? []}
      capabilities={capabilities}
      resources={resources}
      approval={firstAction ? <ActionApprovalCard action={firstAction} busy={action.isPending} error={action.error instanceof Error ? action.error.message : null} onApprove={() => action.mutate('approve')} onCancel={() => action.mutate('cancel')} /> : undefined}
      announcement={jobMutation.isSuccess ? 'Job intent updated.' : revoke.isSuccess ? 'Host revoked.' : undefined}
      onPause={record.job ? () => jobMutation.mutate('pause') : undefined}
      onCancel={record.job ? () => jobMutation.mutate('cancel') : undefined}
      onRevoke={(host) => revoke.mutate(host)}
      onLoadArtifact={async (artifactId) => {
        const loaded = await remoteApi.artifact(artifactId)
        return mapArtifact(loaded.artifact, loaded.textContent)
      }}
      activityUnavailable
    />
  )
}