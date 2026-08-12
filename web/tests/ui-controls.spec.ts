import { expect, test, type Page, type Route } from '@playwright/test'

const pageOf = <T>(items: T[]) => ({ items, nextCursor: null })
const setup = {
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

async function fulfill(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

async function signIn(page: Page) {
  await page.goto('/')
  await page.getByLabel('Developer sign-in (local only)').fill('alice@example.com')
  await page.getByRole('button', { name: /continue/i }).click()
  await expect(page).toHaveURL(/\/chat$/)
}

test('delivered primary controls have real targets, accessible names, and no console failures', async ({ page }) => {
  const consoleErrors: string[] = []
  page.on('console', (message) => {
    if (message.type() === 'error') consoleErrors.push(message.text())
  })
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (path.endsWith('/setup/status')) return fulfill(route, setup)
    if (path.endsWith('/settings/model-profiles')) return fulfill(route, pageOf([]))
    if (path.endsWith('/settings/model-gateways')) return fulfill(route, { items: [{ id: 'homelab', displayName: 'Homelab LiteLLM' }] })
    if (path.endsWith('/settings')) return fulfill(route, { defaultChatModelProfileId: null, defaultLightweightModelProfileId: null, timezone: 'Europe/Bucharest', approvalDefaults: {}, memoryControls: {}, version: 1 })
    if (path.endsWith('/integrations/sources')) return fulfill(route, { items: [{ id: 'local', name: 'Installed and local', state: 'READY', errorCode: null }] })
    if (path.endsWith('/accounts/regina-maria/connectors')) return fulfill(route, { items: [] })
    if (path.endsWith('/conversations') || path.endsWith('/accounts') || path.endsWith('/plugins') || path.endsWith('/capabilities') || path.endsWith('/jobs') || path.endsWith('/memory') || path.endsWith('/activity') || path.endsWith('/actions')) return fulfill(route, pageOf([]))
    return fulfill(route, { code: 'not_found' }, 404)
  })
  await signIn(page)

  const routes = [
    ['/chat', 'Chat'],
    ['/jobs', 'Jobs'],
    ['/accounts', 'Accounts'],
    ['/activity', 'Activity & access'],
    ['/plugins', 'Plugins'],
    ['/memory', 'Memory'],
    ['/settings', 'Settings'],
  ] as const

  for (const [path, navName] of routes) {
    await page.goto(path)
    await expect(page.getByRole('heading').first()).toBeVisible()
    await expect(page.locator('a[href="#"]:visible')).toHaveCount(0)
    await expect(page.getByText(/coming soon|not implemented|arrives in a later phase/i)).toHaveCount(0)
    const unnamedButtons = await page.locator('button:visible').evaluateAll((buttons) =>
      buttons.filter((button) => {
        const label = button.getAttribute('aria-label') || button.getAttribute('title') || button.textContent
        return !label?.trim()
      }).length,
    )
    expect(unnamedButtons, `${path} has unnamed visible buttons`).toBe(0)
    const menu = page.getByRole('button', { name: 'Open navigation' })
    if (await menu.isVisible()) await menu.click()
    await expect(page.getByRole('link', { name: navName })).toBeVisible()
  }

  expect(consoleErrors).toEqual([])
})
