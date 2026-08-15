import { expect, test, type Page, type Route } from '@playwright/test'

const pageOf = <T>(items: T[]) => ({ items, nextCursor: null })
const setup = {
  server: { state: 'CONNECTED', displayName: 'Tessera Home', version: '0.1.0' },
  ai: { state: 'CONNECTED', gatewayId: 'homelab', displayName: 'Homelab', model: 'alpha', profileId: 'profile-1', detailCode: null },
  integrations: [], canOpenChat: true, requiredActionCount: 1,
}
const host = {
  hostId: 'host-main', displayName: 'Home Mac', platform: 'macOS', architecture: 'arm64', lifecycle: 'BUSY', connectionStatus: 'CONNECTED',
  agentVersion: '1.0.0', protocolVersion: '1', lastSeenAt: '2026-08-14T12:00:00Z', pairedAt: '2026-08-14T11:00:00Z', revokedAt: null, version: 3,
}
const jobRun = { id: 'run-main', runId: 'run-main', jobId: 'job-main', scheduledFor: '2026-08-14T12:00:00Z', state: 'WAITING_FOR_APPROVAL', startedAt: null, endedAt: null, modelProfileId: null, contextSnapshotRef: null, capabilityCallIds: [], accountIds: [], actionIds: ['action-1'], outputRefs: [], evidenceRefs: [], errorCode: null, version: 2 }
const job = { id: 'job-main', jobId: 'job-main', name: 'Inspect repository', instruction: 'Read repository identity', desiredState: 'ACTIVE', health: 'WAITING', modelProfileId: null, schedule: { kind: 'once', at: null, localTime: null, timeZone: 'UTC', days: null }, nextOccurrence: null, accountGrants: [], capabilityGrants: [], sideEffectGrants: [], contextPolicy: {}, lastRun: jobRun, kind: 'DEVELOPMENT', conversationId: 'conversation-1', developmentSpec: null, version: 4 }
const action = { id: 'action-1', conversationId: null, messageId: null, jobId: job.id, jobRunId: jobRun.id, pluginId: 'github', pluginVersion: '1', capabilityId: 'github.issues.create', capabilityVersion: '1', accountId: null, target: 'owner/repo', payloadPreview: { title: 'Review Remote result' }, state: 'PROPOSED', expiresAt: '2026-08-14T13:00:00Z', providerReceipt: null, verificationState: null, failureCode: null, version: 1 }
const artifact = { artifactId: 'artifact-1', runId: jobRun.id, leaseId: 'lease-1', actionId: null, kind: 'TEXT', mediaType: 'text/plain', summary: 'Repository identity', sizeBytes: 80, sha256: 'a'.repeat(64), retention: 'RUN', contentState: 'AVAILABLE', redacted: false, truncated: false, createdAt: '2026-08-14T12:01:00Z', expiresAt: null, version: 1 }

async function fulfill(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

async function signIn(page: Page) {
  await page.route('**/api/v1/setup/status', (route) => fulfill(route, setup))
  await page.goto('/')
  await page.getByLabel('Developer sign-in (local only)').fill('alice@example.com')
  await page.getByRole('button', { name: /continue/i }).click()
  await expect(page).toHaveURL(/\/chat$/)
}

test('Remote supervises the same canonical Host and Job on desktop and phone', async ({ page }, testInfo) => {
  let artifactReads = 0
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (path.endsWith('/setup/status')) return fulfill(route, setup)
    if (path.endsWith('/hosts')) return fulfill(route, pageOf([host]))
    if (path.endsWith('/hosts/host-main')) return fulfill(route, {
      host,
      capabilities: [{ capabilityId: 'host.repo.identity', capabilityVersion: '1', schemaHash: 'b'.repeat(64), sideEffectClass: 'READ_ONLY', advertisedAt: host.pairedAt }],
      capabilityGrants: [{ capabilityId: 'host.repo.identity', capabilityVersion: '1', grantedAt: host.pairedAt, revokedAt: null, version: 1 }],
      resources: [{ resourceId: 'repo-main', type: 'REPOSITORY', displayName: 'Tessera', fingerprint: 'c'.repeat(64), state: 'AVAILABLE', advertisedAt: host.pairedAt, version: 1 }],
      resourceGrants: [{ resourceId: 'repo-main', accessMode: 'READ_ONLY', grantedAt: host.pairedAt, revokedAt: null, version: 1 }],
    })
    if (path.endsWith('/jobs')) return fulfill(route, pageOf([job]))
    if (path.endsWith('/actions')) return fulfill(route, pageOf([action]))
    if (path.endsWith('/job-runs/run-main/remote')) return fulfill(route, {
      blocker: { code: 'WAITING_FOR_HOST', hostId: host.hostId, capabilityId: null, resourceId: null, detailCode: 'host_disconnected', observedAt: host.lastSeenAt, clearedAt: null, version: 1 },
      lease: null, host, checkpoints: [{ sequence: 1, step: 'JOB_ACCEPTED', stateJson: '{}', fence: 1, createdAt: '2026-08-14T12:00:30Z' }], artifacts: [artifact],
    })
    if (path.endsWith('/host-artifacts/artifact-1')) { artifactReads += 1; return fulfill(route, { artifact, textContent: '{"branch":"main","commit":"abc"}' }) }
    return fulfill(route, { code: 'not_found' }, 404)
  })

  await signIn(page)
  if (testInfo.project.name === 'phone') {
    const menu = page.getByRole('button', { name: 'Open navigation' })
    await expect(menu).toBeVisible()
    await menu.click()
    const navigation = page.getByRole('dialog', { name: 'Navigation' })
    await expect(navigation).toBeVisible()
    await navigation.getByRole('link', { name: 'Remote' }).click()
  } else await page.getByRole('link', { name: 'Remote' }).click()
  await expect(page.getByRole('heading', { name: 'Remote Host preview' })).toBeVisible()
  await expect(page.getByText('Pairing remains unavailable in this preview')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Pair a Mac' })).toBeDisabled()
  await expect(page.getByRole('link', { name: 'Home Mac' }).first()).toBeVisible()
  expect(artifactReads).toBe(0)
  await page.screenshot({ path: `../.architrave/runs/20260813-remote-hosts-final/legibility/remote-${testInfo.project.name}-inventory.png`, fullPage: true })

  await page.getByRole('link', { name: 'Home Mac' }).first().click()
  await expect(page.getByRole('heading', { name: 'Home Mac' })).toBeVisible()
  await expect(page.getByText('Inspect repository')).toBeVisible()
  await expect(page.getByRole('link', { name: 'View work' })).toHaveAttribute('href', '/jobs?jobId=job-main&runId=run-main')
  await expect(page.getByText('Waiting for Home Mac to reconnect. The Job remains durable.')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Action required' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Approve exact action' })).toBeVisible()
  await expect(page.getByText('Repository identity')).toBeVisible()
  expect(artifactReads).toBe(0)
  await page.screenshot({ path: `../.architrave/runs/20260813-remote-hosts-final/legibility/remote-${testInfo.project.name}-detail.png`, fullPage: true })

  await page.getByRole('button', { name: 'Preview' }).click()
  await expect(page.getByText('{"branch":"main","commit":"abc"}')).toBeVisible()
  expect(artifactReads).toBe(1)
})