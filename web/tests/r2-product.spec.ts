import { expect, test, type Page, type Route } from '@playwright/test'

const pageOf = <T>(items: T[]) => ({ items, nextCursor: null })
const connectedSetup = {
  server: { state: 'CONNECTED', displayName: 'Tessera Home', version: '0.1.0' },
  ai: { state: 'CONNECTED', gatewayId: 'homelab', displayName: 'Homelab LiteLLM', model: 'claude-haiku-4.5', profileId: 'profile-1', detailCode: null },
  integrations: [
    { id: 'github', name: 'GitHub', state: 'READY_TO_CONNECT', runtimeState: 'READY', accountId: null, accountHealth: null, detailCode: 'account_authorization_required', connectPath: '/accounts' },
    { id: 'gmail', name: 'Gmail', state: 'READY_TO_CONNECT', runtimeState: 'READY', accountId: null, accountHealth: null, detailCode: 'account_authorization_required', connectPath: '/accounts' },
    { id: 'regina-maria', name: 'Regina Maria', state: 'READY_TO_CONNECT', runtimeState: 'READY', accountId: null, accountHealth: null, detailCode: 'account_authorization_required', connectPath: '/accounts' },
  ],
  canOpenChat: true,
  requiredActionCount: 3,
}

async function signIn(page: Page) {
  await page.route('**/api/v1/setup/status', (route) => fulfill(route, connectedSetup))
  await page.goto('/')
  await page.getByLabel('Developer sign-in (local only)').fill('alice@example.com')
  await page.getByRole('button', { name: /continue/i }).click()
  await expect(page).toHaveURL(/\/chat$/)
}

test('first run automatically bootstraps the detected homelab AI gateway', async ({ page }) => {
  const profile = { profileId: 'profile-1', accountId: 'model-account', adapterKind: 'openai-compatible-local', endpoint: 'internal', model: 'claude-haiku-4.5', contextLimit: 200000, enabled: true, streamingSupported: true, toolSupport: true, version: 1 }
  let bootstrapped = false
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (path.endsWith('/setup/status')) return fulfill(route, bootstrapped ? connectedSetup : { ...connectedSetup, ai: { ...connectedSetup.ai, state: 'READY_TO_CONNECT', profileId: null }, canOpenChat: false, requiredActionCount: 4 })
    if (path.endsWith('/setup/bootstrap')) { bootstrapped = true; return fulfill(route, connectedSetup) }
    if (path.endsWith('/settings/model-profiles')) return fulfill(route, pageOf(bootstrapped ? [profile] : []))
    if (path.endsWith('/settings')) return fulfill(route, { defaultChatModelProfileId: 'profile-1', defaultLightweightModelProfileId: 'profile-1', timezone: 'Europe/Bucharest', approvalDefaults: {}, memoryControls: {}, version: 1 })
    if (path.endsWith('/conversations') || path.endsWith('/accounts') || path.endsWith('/capabilities')) return fulfill(route, pageOf([]))
    return fulfill(route, { code: 'not_found' }, 404)
  })
  await page.goto('/')
  await page.getByLabel('Developer sign-in (local only)').fill('alice@example.com')
  await page.getByRole('button', { name: /continue/i }).click()
  await expect.poll(() => bootstrapped).toBe(true)
  await expect(page.getByText('What should Tessera help with?')).toBeVisible()
  await expect(page.getByText('Model configuration required')).toHaveCount(0)
})

test('Plugins searches installed, official registry, and GitHub metadata without executing results', async ({ page }) => {
  await page.addInitScript(() => { window.open = ((url?: string | URL) => { sessionStorage.setItem('inspected-integration', String(url)); return null }) as typeof window.open })
  const plugin = { id: 'regina-maria', pluginId: 'regina-maria', name: 'Regina Maria', version: '1.0.0', pluginVersion: '1.0.0', publisher: 'Tessera', enabled: true, packageHash: 'hash', configurationState: 'ACCOUNT_SCOPED', accountProviderIds: ['regina-maria'], capabilities: [], versionStamp: 1 }
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (path.endsWith('/settings/model-profiles') || path.endsWith('/conversations') || path.endsWith('/accounts') || path.endsWith('/capabilities')) return fulfill(route, pageOf([]))
    if (path.endsWith('/plugins')) return fulfill(route, pageOf([plugin]))
    if (path.endsWith('/integrations/sources')) return fulfill(route, { items: [{ id: 'local', name: 'Installed and local', state: 'READY', errorCode: null }, { id: 'mcp-registry', name: 'Official MCP Registry', state: 'READY', errorCode: null }, { id: 'github', name: 'GitHub public repositories', state: 'READY', errorCode: null }] })
    if (path.endsWith('/integrations/search')) return fulfill(route, { items: [{ id: 'github:homeassistant-ai/ha-mcp', name: 'ha mcp', description: 'Home Assistant MCP server', source: 'github', publisher: 'homeassistant-ai', runtime: 'MCP candidate', repositoryOrPackage: 'https://github.com/homeassistant-ai/ha-mcp', version: 'main', license: 'MIT', trustLevel: 'UNTRUSTED', capabilitiesSummary: ['Home Assistant MCP server'], authTypes: [], sensitivity: 'STANDARD', installationMode: 'SERVER_REVIEW_REQUIRED', installState: 'REVIEW_REQUIRED', installed: false, inspectUrl: 'https://github.com/homeassistant-ai/ha-mcp' }, { id: 'registry:unsafe', name: 'Unsafe metadata', description: 'Registry result whose source URL failed server validation.', source: 'mcp-registry', publisher: 'unknown', runtime: 'MCP', repositoryOrPackage: null, version: '1.0.0', license: null, trustLevel: 'UNTRUSTED', capabilitiesSummary: [], authTypes: [], sensitivity: 'STANDARD', installationMode: 'SERVER_REVIEW_REQUIRED', installState: 'REVIEW_REQUIRED', installed: false, inspectUrl: null }], sources: [{ id: 'github', name: 'GitHub public repositories', state: 'READY', errorCode: null }] })
    return fulfill(route, { code: 'not_found' }, 404)
  })
  await signIn(page)
  await page.goto('/plugins')
  await page.getByLabel('Search integrations').fill('home assistant')
  await page.getByRole('button', { name: 'Search', exact: true }).click()
  await expect(page.getByText('Home Assistant MCP server', { exact: true })).toBeVisible()
  await expect(page.locator('[data-product-state="UNTRUSTED"]')).toHaveCount(2)
  await expect(page.getByRole('button', { name: 'Install' })).toHaveCount(0)
  await expect(page.getByText('Public source URL unavailable.')).toBeVisible()
  await expect(page.getByText('Built into the reviewed Tessera server image.')).toHaveCount(0)
  await page.getByRole('button', { name: 'Inspect source' }).click()
  await expect.poll(() => page.evaluate(() => sessionStorage.getItem('inspected-integration'))).toBe('https://github.com/homeassistant-ai/ha-mcp')
})

test('Plugins reviews and installs only a hash-validated local package in a disabled state', async ({ page }) => {
  let installed = false
  let installCalls = 0
  const plugin = { id: 'local-tools', pluginId: 'local-tools', name: 'Local tools', version: '1.2.3', pluginVersion: '1.2.3', publisher: 'Tessera', enabled: false, packageHash: 'reviewed-hash', configurationState: 'READY', accountProviderIds: [], capabilities: [], versionStamp: 1 }
  const result = { id: 'local-tools', name: 'Local tools', description: 'Reviewed local utility capabilities.', source: 'local', publisher: 'Tessera', runtime: 'Tessera plugin', repositoryOrPackage: null, version: '1.2.3', license: null, trustLevel: 'BUILT_IN', capabilitiesSummary: ['Read local time'], authTypes: [], sensitivity: 'STANDARD', installationMode: 'SERVER_INSTALLED', installState: installed ? 'INSTALLED' : 'AVAILABLE', installed, inspectUrl: null }
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (path.endsWith('/settings/model-profiles') || path.endsWith('/conversations') || path.endsWith('/accounts') || path.endsWith('/capabilities')) return fulfill(route, pageOf([]))
    if (path.endsWith('/plugins')) return fulfill(route, pageOf(installed ? [plugin] : []))
    if (path.endsWith('/integrations/sources')) return fulfill(route, { items: [{ id: 'local', name: 'Installed and local', state: 'READY', errorCode: null }] })
    if (path.endsWith('/integrations/search')) return fulfill(route, { items: [{ ...result, installState: installed ? 'INSTALLED' : 'AVAILABLE', installed }], sources: [{ id: 'local', name: 'Installed and local', state: 'READY', errorCode: null }] })
    if (path.endsWith('/integrations/local/local-tools/versions/1.2.3/install') && route.request().method() === 'POST') {
      installCalls += 1
      installed = true
      return fulfill(route, { pluginId: 'local-tools', version: '1.2.3', installState: 'INSTALLED' })
    }
    return fulfill(route, { code: 'not_found' }, 404)
  })
  await signIn(page)
  await page.goto('/plugins')
  await page.getByLabel('Search integrations').fill('local tools')
  await page.getByRole('button', { name: 'Search', exact: true }).click()
  await page.getByRole('button', { name: 'Review installation' }).click()
  await expect(page.getByRole('heading', { name: 'Install Local tools' })).toBeVisible()
  await expect(page.getByText(/disabled state/)).toBeVisible()
  await expect(page.getByRole('dialog').getByText('Read local time', { exact: false })).toBeVisible()
  await page.getByRole('button', { name: 'Install disabled' }).click()
  await expect.poll(() => installCalls).toBe(1)
  await expect(page.getByRole('button', { name: 'Enable' })).toBeVisible()
})

test('Jobs reject past schedules and require confirmation before cancellation', async ({ page }) => {
  const profile = { profileId: 'profile-1', accountId: 'model-account', adapterKind: 'openai-compatible-local', endpoint: 'internal', model: 'alpha', contextLimit: 8192, enabled: true, streamingSupported: true, toolSupport: true, version: 1 }
  const job = { id: 'job-1', jobId: 'job-1', name: 'Morning brief', instruction: 'Summarize', desiredState: 'ACTIVE', health: 'READY', modelProfileId: 'profile-1', schedule: { kind: 'daily', at: null, localTime: '08:00', timeZone: 'Europe/Bucharest', days: null }, nextOccurrence: null, accountGrants: ['model-account'], capabilityGrants: ['model.chat.complete@1'], sideEffectGrants: [], contextPolicy: {}, lastRun: null, version: 1 }
  let canceled = false
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (path.endsWith('/settings/model-profiles')) return fulfill(route, pageOf([profile]))
    if (path.endsWith('/settings')) return fulfill(route, { defaultChatModelProfileId: 'profile-1', defaultLightweightModelProfileId: 'profile-1', timezone: 'Europe/Bucharest', approvalDefaults: {}, memoryControls: {}, version: 1 })
    if (path.endsWith('/accounts') || path.endsWith('/capabilities')) return fulfill(route, pageOf([]))
    if (path.endsWith('/jobs/job-1') && route.request().method() === 'DELETE') { canceled = true; return fulfill(route, { ...job, desiredState: 'CANCELED', version: 2 }) }
    if (path.endsWith('/jobs')) return fulfill(route, pageOf([job]))
    return fulfill(route, { code: 'not_found' }, 404)
  })
  await signIn(page)
  await page.goto('/jobs')
  await page.getByLabel('Name').fill('Past Job')
  await page.getByLabel('Instruction').fill('Should not schedule in the past')
  await page.getByLabel('Run at').fill('2020-01-01T08:00')
  await page.getByRole('button', { name: 'Review and create Job' }).click()
  await expect(page.getByText('Choose a future date and time for this Job.')).toBeVisible()
  await page.getByRole('button', { name: 'Cancel', exact: true }).click()
  await expect(page.getByText(/Existing run history/)).toBeVisible()
  expect(canceled).toBe(false)
  await page.getByRole('button', { name: 'Cancel Job' }).click()
  await expect.poll(() => canceled).toBe(true)
})

async function fulfill(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

test('R2 lands on truthful Chat and exposes primary product navigation', async ({ page }, testInfo) => {
  await page.route('**/api/v1/settings/model-profiles', (route) => fulfill(route, pageOf([])))
  await page.route('**/api/v1/conversations', (route) => fulfill(route, pageOf([])))
  await page.route('**/api/v1/accounts', (route) => fulfill(route, pageOf([])))
  await signIn(page)
  await expect(page.getByRole('heading', { name: 'New conversation' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Model configuration required' })).toBeVisible()
  await page.screenshot({ path: `test-results/screens/${testInfo.project.name}-r2-chat-first-run.png`, fullPage: true })
  const menu = page.getByRole('button', { name: 'Open navigation' })
  if (await menu.isVisible()) await menu.click()
  for (const name of ['Chat', 'Jobs', 'Accounts', 'Plugins', 'Memory', 'Activity & access', 'Settings']) {
    await expect(page.getByRole('link', { name })).toBeVisible()
  }
})

test('Memory exposes source-grounded Why, correction, and stop-using language', async ({ page }) => {
  let memory = { assertionId: 'memory-1', subjectKey: 'user', predicate: 'preference', value: 'morning appointments', status: 'Current', validFrom: '2026-08-10T08:00:00Z', validTo: null, evidenceRefs: ['evidence-user-1'], version: 1 }
  await page.route('**/api/v1/**', async (route) => {
    const url = new URL(route.request().url())
    if (url.pathname.endsWith('/settings/model-profiles')) return fulfill(route, pageOf([]))
    if (url.pathname.endsWith('/conversations')) return fulfill(route, pageOf([]))
    if (url.pathname.endsWith('/accounts')) return fulfill(route, pageOf([]))
    if (url.pathname.endsWith('/memory/memory-1/why')) return fulfill(route, { assertionId: memory.assertionId, current: memory, previous: { ...memory, value: 'no preference', status: 'Superseded' }, evidence: [{ evidenceId: 'evidence-user-1', sourceType: 'user.explicit', sourceLocator: 'tessera://memory', observedAt: '2026-08-10T08:00:00Z', sourceTimestamp: '2026-08-10T08:00:00Z', boundedExcerpt: 'morning appointments' }], lineageRefs: ['memory-old'] })
    if (url.pathname.endsWith('/memory/memory-1/history')) return fulfill(route, pageOf([{ assertionId: 'memory-old' }, { assertionId: memory.assertionId }]))
    if (url.pathname.endsWith('/memory/memory-1/correct')) { memory = { ...memory, value: 'afternoon appointments' }; return fulfill(route, memory, 201) }
    if (url.pathname.endsWith('/memory/memory-1/stop-using')) { memory = { ...memory, status: 'Rejected' }; return fulfill(route, memory) }
    if (url.pathname.endsWith('/memory')) return fulfill(route, pageOf([memory]))
    return fulfill(route, { code: 'not_found' }, 404)
  })
  await signIn(page)
  await page.goto('/memory')
  await expect(page.getByText('morning appointments')).toBeVisible()
  await page.getByRole('button', { name: 'Why / Correct' }).click()
  await expect(page.getByText('evidence-user-1')).toBeVisible()
  await expect(page.getByText('History entries: 2')).toBeVisible()
  await page.getByLabel('Corrected value').fill('afternoon appointments')
  await page.getByRole('button', { name: 'Save correction' }).click()
  await expect(page.getByText('afternoon appointments')).toBeVisible()
  await page.getByRole('button', { name: 'Why / Correct' }).click()
  await page.getByRole('button', { name: 'Stop using in context' }).click()
  await expect(page.getByText(/Rejected/)).toBeVisible()
})

test('Chat renders and approves the exact durable Action proposal', async ({ page }, testInfo) => {
  const profile = { profileId: 'profile-1', accountId: 'model-account', adapterKind: 'openai-compatible-remote', endpoint: 'https://model.example/v1', model: 'alpha', contextLimit: 8192, enabled: true, streamingSupported: true, toolSupport: true, version: 1 }
  const conversation = { id: 'conversation-1', conversationId: 'conversation-1', title: 'Alpha work', state: 'ACTIVE', modelProfileId: 'profile-1', createdAt: '2026-08-10T08:00:00Z', updatedAt: '2026-08-10T08:00:00Z', version: 1 }
  const account = { id: 'github-account', accountId: 'github-account', providerId: 'github', pluginId: 'github', displayName: 'Work GitHub', providerAccountId: '42', identityHint: 'alice', lifecycle: 'CONNECTED', permissions: ['issues:write'], providerScopes: [], capabilityIds: ['github.issues.create'], health: 'HEALTHY', lastSuccessfulUse: null, version: 1 }
  let action = { id: 'action-1', conversationId: conversation.id, messageId: null, jobId: null, jobRunId: null, pluginId: 'github', pluginVersion: '1.0.0', capabilityId: 'github.issues.create', capabilityVersion: '1', accountId: account.id, target: 'owner/sandbox', payloadPreview: { title: 'Alpha review', body: 'Exact body' }, state: 'PROPOSED', expiresAt: '2026-08-10T18:00:00Z', providerReceipt: null, verificationState: null, failureCode: null, version: 0 }
  let capabilityUsed = false
  await page.route('**/api/v1/**', async (route) => {
    const url = new URL(route.request().url())
    if (url.pathname.endsWith('/settings/model-profiles')) return fulfill(route, pageOf([profile]))
    if (url.pathname.endsWith('/conversations')) return fulfill(route, pageOf([conversation]))
    if (url.pathname.endsWith('/conversations/conversation-1/messages')) return fulfill(route, pageOf([{ id: 'message-1', messageId: 'message-1', conversationId: conversation.id, role: 'USER', status: 'PERSISTED', parts: [{ id: 'part-1', kind: 'TEXT', text: 'Create the issue', capabilityCallId: null, capabilityResultId: null, actionId: null, evidenceRefs: [], errorCode: null }], createdAt: '2026-08-10T08:00:00Z', completedAt: null, retryOf: null, version: 1 }, ...(capabilityUsed ? [{ id: 'message-capability', messageId: 'message-capability', conversationId: conversation.id, role: 'CAPABILITY', status: 'COMPLETED', parts: [{ id: 'part-capability', kind: 'CAPABILITY_RESULT', text: '{"timeZone":"UTC"}', capabilityCallId: null, capabilityResultId: 'execution-time', actionId: null, evidenceRefs: ['evidence:capability:execution-time'], errorCode: null }], createdAt: '2026-08-10T08:01:00Z', completedAt: '2026-08-10T08:01:00Z', retryOf: null, version: 1 }] : [])]))
    if (url.pathname.endsWith('/accounts')) return fulfill(route, pageOf([account]))
    if (url.pathname.endsWith('/capabilities/local.time/invoke')) { capabilityUsed = true; return fulfill(route, { executionId: 'execution-time', result: { timeZone: 'UTC' }, evidenceRefs: ['evidence:capability:execution-time'] }) }
    if (url.pathname.endsWith('/capabilities')) return fulfill(route, pageOf([{ id: 'local.time', version: '1', pluginId: 'local', description: 'Current date and time', accountRequired: false, requiredPermissions: [], sideEffectClass: 'ReadOnly', available: true, blockedCode: null }]))
    if (url.pathname.endsWith('/actions/action-1/approve')) { action = { ...action, state: 'EXTERNALLY_CONFIRMED', providerReceipt: 'github-42', verificationState: 'provider_verified', version: 5 }; return fulfill(route, action, 202) }
    if (url.pathname.endsWith('/actions')) return fulfill(route, pageOf([action]))
    return fulfill(route, { code: 'not_found' }, 404)
  })
  await signIn(page)
  await expect(page.getByText('owner/sandbox')).toBeVisible()
  await expect(page.getByText(/Alpha review/)).toBeVisible()
  await page.screenshot({ path: `test-results/screens/${testInfo.project.name}-r2-action-approval.png`, fullPage: true })
  await page.getByRole('button', { name: 'Approve exact action' }).click()
  await expect(page.locator('[data-product-state="EXTERNALLY_CONFIRMED"]')).toBeVisible()
  await expect(page.getByText(/github-42/)).toBeVisible()
  await page.getByRole('button', { name: 'Current date and time' }).click()
  await expect(page.getByText('{"timeZone":"UTC"}')).toBeVisible()
})

test('Chat grants an arbitrary integration from account capability metadata', async ({ page }) => {
  const profile = { profileId: 'profile-1', accountId: 'model-account', adapterKind: 'openai-compatible-remote', endpoint: 'https://model.example/v1', model: 'alpha', contextLimit: 8192, enabled: true, streamingSupported: true, toolSupport: true, version: 1 }
  const conversation = { id: 'conversation-1', conversationId: 'conversation-1', title: 'Calendar planning', state: 'ACTIVE', modelProfileId: 'profile-1', createdAt: '2026-08-10T08:00:00Z', updatedAt: '2026-08-10T08:00:00Z', version: 1 }
  const account = { id: 'calendar-account', accountId: 'calendar-account', providerId: 'calendar-service', pluginId: 'calendar-mcp', displayName: 'Family calendar', providerAccountId: 'calendar-42', identityHint: 'family', lifecycle: 'CONNECTED', permissions: ['events:read', 'events:write'], providerScopes: [], capabilityIds: ['calendar.events.list', 'calendar.events.create'], health: 'HEALTHY', lastSuccessfulUse: null, version: 1 }
  let grants = { accountGrants: [] as string[], capabilityGrants: [] as string[], version: 1 }
  let submitted: Record<string, unknown> | null = null
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (path.endsWith('/settings/model-profiles')) return fulfill(route, pageOf([profile]))
    if (path.endsWith('/conversations/conversation-1/active-execution')) return fulfill(route, { executionId: '', userMessageId: '', modelProfileId: '' })
    if (path.endsWith('/conversations/conversation-1/messages')) return fulfill(route, pageOf([]))
    if (path.endsWith('/conversations/conversation-1/grants')) {
      if (route.request().method() === 'PUT') {
        submitted = route.request().postDataJSON() as Record<string, unknown>
        grants = { accountGrants: ['calendar-account'], capabilityGrants: ['calendar.events.list@1', 'calendar.events.create@1'], version: 2 }
      }
      return fulfill(route, grants)
    }
    if (path.endsWith('/conversations')) return fulfill(route, pageOf([conversation]))
    if (path.endsWith('/accounts')) return fulfill(route, pageOf([account]))
    if (path.endsWith('/capabilities')) return fulfill(route, pageOf([]))
    if (path.endsWith('/actions')) return fulfill(route, pageOf([]))
    return fulfill(route, { code: 'not_found' }, 404)
  })

  await signIn(page)
  await page.getByLabel('Integration account for this conversation').selectOption('calendar-account')
  await page.getByRole('button', { name: 'Allow integration tools' }).click()
  await expect.poll(() => submitted).toMatchObject({
    accountGrants: ['calendar-account'],
    capabilityGrants: [
      { id: 'calendar.events.list', version: '1' },
      { id: 'calendar.events.create', version: '1' },
    ],
  })
  await expect(page.getByRole('button', { name: 'Integration allowed' })).toBeVisible()
})

test('Chat voice negotiates SDP only, persists captions, and stops local media', async ({ page }) => {
  await page.addInitScript(() => {
    const state = { permissionCalls: 0, peerCalls: 0, addTrackCalls: 0, dataChannelCalls: 0, offerCalls: 0, localDescriptionCalls: 0, stopped: false, channel: null as null | { onmessage?: (event: { data: string }) => void }, sent: [] as string[], fetches: [] as string[] }
    const originalFetch = globalThis.fetch.bind(globalThis)
    globalThis.fetch = ((input: RequestInfo | URL, init?: RequestInit) => { state.fetches.push(String(input)); return originalFetch(input, init) }) as typeof fetch
    class FakeTrack { enabled = true; stop() { state.stopped = true } }
    const track = new FakeTrack()
    const stream = { getTracks: () => [track], getAudioTracks: () => [track] }
    Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: { getUserMedia: async () => { state.permissionCalls += 1; return stream } } })
    class FakeChannel {
      readyState = 'open'; onopen?: () => void; onmessage?: (event: { data: string }) => void; onerror?: () => void
      constructor() { state.channel = this }
      send(value: string) { state.sent.push(value) }
      close() { this.readyState = 'closed' }
    }
    class FakePeer {
      connectionState = 'new'; onconnectionstatechange?: () => void; ontrack?: () => void; channel = new FakeChannel()
      constructor() { state.peerCalls += 1 }
      addTrack() { state.addTrackCalls += 1; return {} }
      createDataChannel() { state.dataChannelCalls += 1; return this.channel }
      async createOffer() { state.offerCalls += 1; return { type: 'offer', sdp: 'v=0\r\nm=audio 9 RTP/AVP 111\r\n' } }
      async setLocalDescription() { state.localDescriptionCalls += 1 }
      async setRemoteDescription() { this.connectionState = 'connected'; this.onconnectionstatechange?.(); this.channel.onopen?.() }
      close() { this.connectionState = 'closed' }
    }
    Object.defineProperty(globalThis, 'RTCPeerConnection', { configurable: true, value: FakePeer })
    Object.defineProperty(globalThis, '__tesseraVoiceTest', { configurable: true, value: state })
  })
  const profile = { profileId: 'profile-1', accountId: 'model-account', adapterKind: 'openai-compatible-remote', endpoint: 'https://model.example/v1', model: 'alpha', contextLimit: 8192, enabled: true, streamingSupported: true, toolSupport: true, version: 1 }
  const conversation = { id: 'conversation-voice', conversationId: 'conversation-voice', title: 'Voice thread', state: 'ACTIVE', modelProfileId: 'profile-1', createdAt: '2026-08-13T08:00:00Z', updatedAt: '2026-08-13T08:00:00Z', version: 1 }
  let negotiation: Record<string, unknown> | null = null
  let saved = false
  let ended = false
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (path.endsWith('/setup/status')) return fulfill(route, connectedSetup)
    if (path.endsWith('/settings/model-profiles')) return fulfill(route, pageOf([profile]))
    if (path.endsWith('/settings')) return fulfill(route, { defaultChatModelProfileId: profile.profileId, defaultLightweightModelProfileId: profile.profileId, timezone: 'UTC', approvalDefaults: {}, memoryControls: {}, version: 1 })
    if (path.endsWith('/realtime-voice/status')) return fulfill(route, { state: 'READY', blockedCode: null, supportsTools: false, maxSessionSeconds: 900, checkedAt: '2026-08-13T08:00:00Z', validUntil: '2026-08-13T08:05:00Z', version: 1 })
    if (path.endsWith('/conversations/conversation-voice/realtime-sessions') && route.request().method() === 'POST') { negotiation = route.request().postDataJSON() as Record<string, unknown>; return fulfill(route, { sessionId: 'session-1', answerSdp: 'v=0\r\nm=audio 9 RTP/AVP 111\r\n', negotiatedAt: new Date().toISOString(), expiresAt: new Date(Date.now() + 15 * 60 * 1000).toISOString(), maxSessionSeconds: 900 }, 201) }
    if (path.endsWith('/conversations/conversation-voice/realtime-sessions/session-1/turns')) { saved = true; return fulfill(route, { sessionId: 'session-1', clientTurnId: 'turn-1', replayed: false }, 201) }
    if (path.endsWith('/conversations/conversation-voice/realtime-sessions/session-1/end')) { ended = true; return fulfill(route, { id: 'session-1', resourceType: 'realtime_session', version: 2 }) }
    if (path.endsWith('/conversations/conversation-voice/active-execution')) return fulfill(route, null)
    if (path.endsWith('/conversations/conversation-voice/grants')) return fulfill(route, { accountGrants: [], capabilityGrants: [], version: 1 })
    if (path.endsWith('/conversations/conversation-voice/messages')) return fulfill(route, pageOf(saved ? [
      { id: 'voice-user', messageId: 'voice-user', conversationId: conversation.id, role: 'USER', status: 'PERSISTED', parts: [{ id: 'part-user', kind: 'TEXT', text: 'Hello voice', capabilityCallId: null, capabilityResultId: null, actionId: null, evidenceRefs: [], errorCode: null }], createdAt: '2026-08-13T08:00:01Z', completedAt: '2026-08-13T08:00:01Z', retryOf: null, version: 1 },
      { id: 'voice-assistant', messageId: 'voice-assistant', conversationId: conversation.id, role: 'ASSISTANT', status: 'COMPLETED', parts: [{ id: 'part-assistant', kind: 'TEXT', text: 'Hello back', capabilityCallId: null, capabilityResultId: null, actionId: null, evidenceRefs: [], errorCode: null }], createdAt: '2026-08-13T08:00:02Z', completedAt: '2026-08-13T08:00:02Z', retryOf: null, version: 1 },
    ] : []))
    if (path.endsWith('/conversations')) return fulfill(route, pageOf([conversation]))
    if (path.endsWith('/accounts') || path.endsWith('/capabilities') || path.endsWith('/actions')) return fulfill(route, pageOf([]))
    return fulfill(route, { code: 'not_found' }, 404)
  })
  await signIn(page)
  await page.getByRole('button', { name: 'Start voice' }).click()
  await expect.poll(() => page.evaluate(() => (globalThis as unknown as { __tesseraVoiceTest: { permissionCalls: number } }).__tesseraVoiceTest.permissionCalls), { timeout: 15_000 }).toBe(1)
  await expect.poll(() => page.evaluate(() => (globalThis as unknown as { __tesseraVoiceTest: { peerCalls: number } }).__tesseraVoiceTest.peerCalls), { timeout: 15_000 }).toBe(1)
  await expect.poll(() => page.evaluate(() => (globalThis as unknown as { __tesseraVoiceTest: { addTrackCalls: number } }).__tesseraVoiceTest.addTrackCalls), { timeout: 15_000 }).toBe(1)
  await expect.poll(() => page.evaluate(() => (globalThis as unknown as { __tesseraVoiceTest: { dataChannelCalls: number } }).__tesseraVoiceTest.dataChannelCalls), { timeout: 15_000 }).toBe(1)
  await expect.poll(() => page.evaluate(() => (globalThis as unknown as { __tesseraVoiceTest: { offerCalls: number } }).__tesseraVoiceTest.offerCalls), { timeout: 15_000 }).toBe(1)
  await expect.poll(() => page.evaluate(() => (globalThis as unknown as { __tesseraVoiceTest: { localDescriptionCalls: number } }).__tesseraVoiceTest.localDescriptionCalls), { timeout: 15_000 }).toBe(1)
  await expect.poll(() => negotiation, { timeout: 15_000 }).toMatchObject({ offerSdp: 'v=0\r\nm=audio 9 RTP/AVP 111\r\n' })
  expect(negotiation).toEqual(expect.objectContaining({ clientAttemptId: expect.any(String) }))
  expect(negotiation).not.toHaveProperty('endpoint')
  expect(negotiation).not.toHaveProperty('model')
  await expect(page.getByText('Listening', { exact: true })).toBeVisible({ timeout: 15_000 })
  await page.evaluate(() => {
    const state = (globalThis as unknown as { __tesseraVoiceTest: { channel: { onmessage?: (event: { data: string }) => void } } }).__tesseraVoiceTest
    state.channel.onmessage?.({ data: JSON.stringify({ type: 'conversation.item.input_audio_transcription.completed', item_id: 'input-1', transcript: 'Hello voice' }) })
    state.channel.onmessage?.({ data: JSON.stringify({ type: 'response.output_audio_transcript.done', item_id: 'output-1', transcript: 'Hello back' }) })
  })
  const fetches = await page.evaluate(() => (globalThis as unknown as { __tesseraVoiceTest: { fetches: string[] } }).__tesseraVoiceTest.fetches)
  expect(fetches.some((url) => /foundry|openai\.azure\.com/i.test(url))).toBe(false)
  await expect.poll(() => saved).toBe(true)
  await expect(page.getByText('Hello voice', { exact: true })).toBeVisible()
  await expect(page.getByText('Hello back', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'End voice' }).click()
  await expect.poll(() => ended).toBe(true)
  await expect.poll(() => page.evaluate(() => (globalThis as unknown as { __tesseraVoiceTest: { stopped: boolean } }).__tesseraVoiceTest.stopped)).toBe(true)
})

test('Jobs expose durable run history and waiting approval state', async ({ page }, testInfo) => {
  const job = { id: 'job-1', jobId: 'job-1', name: 'Weekly review', instruction: 'Review open FollowUps', desiredState: 'ACTIVE', health: 'READY', modelProfileId: 'profile-1', schedule: { kind: 'weekday', at: null, localTime: '08:00', timeZone: 'UTC', days: [1,2,3,4,5] }, nextOccurrence: '2026-08-11T08:00:00Z', accountGrants: ['github-account'], capabilityGrants: ['github.issues.create@1'], sideEffectGrants: ['ExternalCommunication'], contextPolicy: {}, lastRun: null, version: 1 }
  const run = { id: 'run-1', runId: 'run-1', jobId: 'job-1', scheduledFor: '2026-08-10T08:00:00Z', state: 'WAITING_FOR_APPROVAL', startedAt: '2026-08-10T08:00:01Z', endedAt: null, modelProfileId: 'profile-1', contextSnapshotRef: null, capabilityCallIds: [], accountIds: ['github-account'], actionIds: ['action-1'], outputRefs: [], evidenceRefs: [], errorCode: null, version: 2 }
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (path.endsWith('/settings/model-profiles')) return fulfill(route, pageOf([]))
    if (path.endsWith('/accounts')) return fulfill(route, pageOf([]))
    if (path.endsWith('/capabilities')) return fulfill(route, pageOf([]))
    if (path.endsWith('/jobs/job-1/runs')) return fulfill(route, pageOf([run]))
    if (path.endsWith('/job-runs/run-1')) return fulfill(route, { run, contextSnapshot: null, capabilityUses: pageOf([]), accountUses: pageOf([]), actions: pageOf([]), outputs: pageOf([]), evidence: pageOf([]), trace: pageOf([{ sequence: 1, occurredAt: '2026-08-10T08:00:01Z', type: 'awaiting_user_approval', summary: 'Waiting for exact user approval', actionId: 'action-1', errorCode: null }]) })
    if (path.endsWith('/jobs')) return fulfill(route, pageOf([job]))
    return fulfill(route, { code: 'not_found' }, 404)
  })
  await signIn(page)
  await page.goto('/jobs')
  await page.getByRole('button', { name: 'History' }).click()
  await page.getByRole('button', { name: /8\/10\/2026/ }).click()
  await expect(page.getByLabel(/Run run-/).locator('[data-product-state="WAITING_FOR_APPROVAL"]')).toBeVisible()
  await expect(page.getByText('Waiting for exact user approval')).toBeVisible()
  await page.screenshot({ path: `test-results/screens/${testInfo.project.name}-r2-jobs.png`, fullPage: true })
})

test('Plugin disable and Account revoke show truthful recovery states', async ({ page }, testInfo) => {
  let plugin = { id: 'github', pluginId: 'github', name: 'GitHub', version: '1.0.0', pluginVersion: '1.0.0', publisher: 'Tessera', enabled: true, packageHash: 'hash', configurationState: 'ACCOUNT_SCOPED', accountProviderIds: ['github'], capabilities: [{ id: 'github.issues.list', version: '1', description: 'List issues', executorKind: 'github-rest', accountRequired: true, requiredPermissions: ['issues:read'], sideEffectClass: 'ReadOnly', timeoutMilliseconds: 30000, maxResultBytes: 32768 }], versionStamp: 1 }
  let account = { id: 'account-1', accountId: 'account-1', providerId: 'github', pluginId: 'github', displayName: 'Work GitHub', providerAccountId: '42', identityHint: 'alice', lifecycle: 'CONNECTED', permissions: ['issues:read'], providerScopes: [], capabilityIds: ['github.issues.list'], health: 'HEALTHY', lastSuccessfulUse: null, version: 1 }
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (path.endsWith('/settings/model-profiles') || path.endsWith('/conversations')) return fulfill(route, pageOf([]))
    if (path.endsWith('/plugins/github/versions/1.0.0/disable')) { plugin = { ...plugin, enabled: false, versionStamp: 2 }; return fulfill(route, plugin) }
    if (path.endsWith('/plugins')) return fulfill(route, pageOf([plugin]))
    if (path.endsWith('/capabilities')) return fulfill(route, pageOf([{ id: 'github.issues.list', version: '1', pluginId: 'github', description: 'List issues', accountRequired: true, requiredPermissions: ['issues:read'], sideEffectClass: 'ReadOnly', available: plugin.enabled, blockedCode: plugin.enabled ? null : 'plugin_disabled' }]))
    if (path.endsWith('/accounts/account-1') && route.request().method() === 'DELETE') { account = { ...account, lifecycle: 'REVOKED', health: 'ERROR', version: 2 }; return fulfill(route, account, 202) }
    if (path.endsWith('/accounts')) return fulfill(route, pageOf([account]))
    return fulfill(route, { code: 'not_found' }, 404)
  })
  await signIn(page)
  await page.goto('/plugins')
  await page.getByRole('button', { name: 'Disable' }).click()
  await expect(page.getByText('plugin disabled')).toBeVisible()
  await page.screenshot({ path: `test-results/screens/${testInfo.project.name}-r2-plugins.png`, fullPage: true })
  await page.goto('/accounts')
  await expect(page.getByRole('option', { name: 'GitHub' })).toBeAttached()
  await page.getByRole('button', { name: 'Revoke' }).click()
  await expect(page.getByText(/immediately blocks Chat and Jobs/i)).toBeVisible()
  await page.getByRole('button', { name: 'Revoke account' }).click()
  await expect(page.getByText('Revoked')).toBeVisible()
  await page.screenshot({ path: `test-results/screens/${testInfo.project.name}-r2-accounts.png`, fullPage: true })
})

test('Gmail Account connection opens Google OAuth without asking for a token',async({page})=>{
  await page.addInitScript(()=>{window.open=((url?:string|URL)=>{sessionStorage.setItem('gmail-oauth-url',String(url));return null}) as typeof window.open})
  const gmailPlugin={id:'gmail',pluginId:'gmail',name:'Gmail',version:'1.0.0',pluginVersion:'1.0.0',publisher:'Tessera',enabled:true,packageHash:'hash',configurationState:'ACCOUNT_SCOPED',accountProviderIds:['gmail'],capabilities:[{id:'gmail.messages.search',version:'1',description:'Search Gmail metadata',executorKind:'gmail-rest',accountRequired:true,requiredPermissions:['gmail.readonly'],sideEffectClass:'ReadOnly',timeoutMilliseconds:30000,maxResultBytes:262144}],versionStamp:1}
  await page.route('**/api/v1/**',async(route)=>{const path=new URL(route.request().url()).pathname;if(path.endsWith('/settings/model-profiles')||path.endsWith('/conversations')||path.endsWith('/accounts'))return fulfill(route,pageOf([]));if(path.endsWith('/plugins'))return fulfill(route,pageOf([gmailPlugin]));if(path.endsWith('/accounts/gmail/connect'))return fulfill(route,{authorizeUrl:'https://accounts.google.com/o/oauth2/v2/auth?state=opaque'});return fulfill(route,{code:'not_found'},404)})
  await signIn(page);await page.goto('/accounts');await page.getByLabel('Account type').selectOption('gmail');await page.getByLabel('Display name').fill('My Gmail');await expect(page.getByLabel('Fine-grained token')).toHaveCount(0);await expect(page.getByLabel('Allowed repositories')).toHaveCount(0);await page.getByRole('button',{name:'Continue with Google'}).click();await expect.poll(()=>page.evaluate(()=>sessionStorage.getItem('gmail-oauth-url'))).toContain('https://accounts.google.com/o/oauth2/v2/auth')
})

test('Regina Maria connection uses configured isolated profiles and reports authorization required',async({page})=>{
  const plugin={id:'regina-maria',pluginId:'regina-maria',name:'Regina Maria',version:'1.0.0',pluginVersion:'1.0.0',publisher:'Tessera',enabled:true,packageHash:'hash',configurationState:'ACCOUNT_SCOPED',accountProviderIds:['regina-maria'],capabilities:[{id:'reginamaria.appointments.list',version:'1',description:'List appointments',executorKind:'reginamaria-mcp',accountRequired:true,requiredPermissions:['reginamaria.appointments.read'],sideEffectClass:'ReadOnly',timeoutMilliseconds:30000,maxResultBytes:262144}],versionStamp:1};let accounts:unknown[]=[]
  await page.route('**/api/v1/**',async(route)=>{const path=new URL(route.request().url()).pathname;if(path.endsWith('/settings/model-profiles')||path.endsWith('/conversations'))return fulfill(route,pageOf([]));if(path.endsWith('/plugins'))return fulfill(route,pageOf([plugin]));if(path.endsWith('/accounts/regina-maria/connectors'))return fulfill(route,{items:[{id:'account-a',displayName:'My Regina Maria'},{id:'account-b',displayName:'Wife - Regina Maria'}]});if(path.endsWith('/accounts/regina-maria/connect')&&route.request().method()==='POST'){accounts=[{id:'rm-b',accountId:'rm-b',providerId:'regina-maria',pluginId:'regina-maria',displayName:'Wife - Regina Maria',providerAccountId:null,identityHint:null,lifecycle:'AUTH_REQUIRED',permissions:['reginamaria.identity','reginamaria.appointments.read'],providerScopes:[],capabilityIds:['reginamaria.account.identity','reginamaria.appointments.list'],health:'AUTH_REQUIRED',lastSuccessfulUse:null,version:2}];return fulfill(route,accounts[0],202)}if(path.endsWith('/accounts'))return fulfill(route,pageOf(accounts));return fulfill(route,{code:'not_found'},404)})
  await signIn(page);await page.goto('/accounts');await page.getByLabel('Account type').selectOption('regina-maria');await expect(page.getByLabel('Fine-grained token')).toHaveCount(0);await expect(page.getByLabel('Authorized profile')).toContainText('Wife - Regina Maria');await page.getByLabel('Authorized profile').selectOption('account-b');await page.getByLabel('Display name').fill('Wife - Regina Maria');await page.getByRole('button',{name:'Connect authorized profile'}).click();await expect(page.getByText(/Authorization required\. The account holder must complete/i)).toBeVisible();await expect(page.getByRole('button',{name:'Test connection'})).toHaveCount(0)
})

test('Settings configures the sole fixed LiteLLM gateway without an internal URL',async({page})=>{
  let submitted:Record<string,unknown>|null=null;const settings={defaultChatModelProfileId:null,defaultLightweightModelProfileId:null,timezone:'Europe/Bucharest',approvalDefaults:{},memoryControls:{},version:1}
  await page.route('**/api/v1/**',async(route)=>{const path=new URL(route.request().url()).pathname;if(path.endsWith('/settings/model-profiles'))return fulfill(route,pageOf([]));if(path.endsWith('/settings/model-gateways')&&route.request().method()==='GET')return fulfill(route,{items:[{id:'homelab',displayName:'Homelab LiteLLM'}]});if(path.endsWith('/settings/model-gateways/connect')){submitted=route.request().postDataJSON() as Record<string,unknown>;return fulfill(route,{profileId:'profile-1',accountId:'model-1',adapterKind:'openai-compatible-local',endpoint:'redacted-in-ui',model:'real-model',contextLimit:32768,enabled:true,streamingSupported:true,toolSupport:true,version:1},201)}if(path.endsWith('/settings')&&route.request().method()==='PATCH')return fulfill(route,{...settings,defaultChatModelProfileId:'profile-1',version:2});if(path.endsWith('/settings'))return fulfill(route,settings);if(path.endsWith('/conversations'))return fulfill(route,pageOf([]));return fulfill(route,{code:'not_found'},404)})
  await signIn(page);await page.goto('/settings');await expect(page.getByLabel('Model endpoint')).toHaveValue('operator-configured');await page.getByLabel('Model name').fill('real-model');await page.getByLabel('Provider token').fill('write-only-key');await page.getByRole('button',{name:'Save and validate model'}).click();await expect.poll(()=>submitted).toMatchObject({gatewayId:'homelab',model:'real-model'});expect(submitted).not.toHaveProperty('endpoint')
})