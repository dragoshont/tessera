import type {
  Action,
  Job,
  Page,
  RemoteApi,
  RemoteHostDetailDto,
  RemoteHostRunProjectionDto,
  RemoteHostSummaryDto,
} from '@tessera/client'
import type { RemoteCurrentJob, RemoteHostSummary } from '../components/remote/RemoteWorkspace'

type ProductApi = {
  jobs(): Promise<Page<Job>>
  actions(query?: string): Promise<Page<Action>>
}

export type RemoteInventoryRecord = {
  source: RemoteHostSummaryDto
  detail: RemoteHostDetailDto | null
  host: RemoteHostSummary
  job: Job | null
  projection: RemoteHostRunProjectionDto | null
  pendingActions: Action[]
}

export type RemoteInventory = {
  records: RemoteInventoryRecord[]
  partial: boolean
  loadedAt: string
}

function currentJob(job: Job | null, actions: Action[]): RemoteCurrentJob | null {
  if (!job?.lastRun) return null
  return {
    runId: job.lastRun.id,
    name: job.name,
    state: job.lastRun.state,
    href: `/jobs?jobId=${encodeURIComponent(job.id)}&runId=${encodeURIComponent(job.lastRun.id)}`,
    pendingApprovals: actions.filter((item) => item.jobRunId === job.lastRun?.id && item.state === 'PROPOSED').length,
  }
}

export function toRemoteHostSummary(
  source: RemoteHostSummaryDto,
  detail: RemoteHostDetailDto | null,
  job: Job | null,
  actions: Action[],
): RemoteHostSummary {
  const activeCapabilities = detail?.capabilityGrants.filter((item) => item.revokedAt === null).length ?? 0
  const activeResources = detail?.resourceGrants.filter((item) => item.revokedAt === null).length ?? 0
  return {
    hostId: source.hostId,
    version: source.version,
    href: `/remote/hosts/${encodeURIComponent(source.hostId)}`,
    displayName: source.displayName,
    platform: source.platform,
    architecture: source.architecture,
    lifecycle: source.lifecycle,
    agentVersion: source.agentVersion,
    protocolVersion: source.protocolVersion,
    statusObservedAt: source.lastSeenAt ?? source.pairedAt,
    lastSeenAt: source.lastSeenAt,
    capabilityCount: activeCapabilities,
    resourceCount: activeResources,
    currentJob: currentJob(job, actions),
  }
}

export async function loadRemoteInventory(remote: RemoteApi, product: ProductApi): Promise<RemoteInventory> {
  const page = await remote.hosts()
  const detailResults = await Promise.allSettled(page.items.map((host) => remote.host(host.hostId)))
  let partial = detailResults.some((result) => result.status === 'rejected')
  let jobs: Job[] = []
  let actions: Action[] = []
  try { jobs = (await product.jobs()).items } catch { partial = true }
  try { actions = (await product.actions('?approvalRequired=true')).items } catch { partial = true }
  const jobsWithRuns = jobs.filter((job) => job.lastRun)
  const projectionResults = await Promise.allSettled(jobsWithRuns.map((job) => remote.runProjection(job.lastRun!.id)))
  partial ||= projectionResults.some((result) => result.status === 'rejected')
  const assignments = projectionResults.flatMap((result, index) => result.status === 'fulfilled' && result.value.host
    ? [{ job: jobsWithRuns[index], projection: result.value }]
    : [])
  const records = page.items.map((source, index) => {
    const detail = detailResults[index].status === 'fulfilled' ? detailResults[index].value : null
    const assignment = assignments.find((item) => item.projection.host?.hostId === source.hostId)
    const pendingActions = actions.filter((item) => item.jobRunId === assignment?.job.lastRun?.id)
    return {
      source,
      detail,
      host: toRemoteHostSummary(source, detail, assignment?.job ?? null, pendingActions),
      job: assignment?.job ?? null,
      projection: assignment?.projection ?? null,
      pendingActions,
    }
  })
  return { records, partial, loadedAt: new Date().toISOString() }
}