import { expect, test, type Page } from '@playwright/test'

// Live hand-off captures. The honest default (no backend) is the fail-closed
// Unavailable state, reached through the real Re-seed entry point. WaitingForYou is
// captured via the page's `?demo=1` affordance (a local placeholder canvas, no real
// worker). Run with:  npx playwright test handoff   (after installing chromium).

async function signIn(page: Page) {
  await page.goto('/')
  // Demo/dev loopback: the developer card signs in as a chosen principal.
  await page.getByLabel('Developer sign-in (local only)').fill('alice@example.com')
  await page.getByRole('button', { name: /continue/i }).click()
  await expect(page).toHaveURL(/\/accounts$/)
}

test('live hand-off — pre-flight then fail-closed Unavailable (the honest default)', async ({
  page,
}, testInfo) => {
  const tag = testInfo.project.name
  await signIn(page)

  // Reach the stage through the real row action (⋯ → Re-seed), client-side so the
  // session survives.
  await page.getByRole('button', { name: 'Actions for Health Portal' }).click()
  await page.getByRole('menuitem', { name: /re-seed/i }).click()
  await expect(page).toHaveURL(/\/handoff\//)

  // Pre-flight dialog.
  await expect(page.getByText('This takes about 2 minutes.')).toBeVisible()
  await page.screenshot({ path: `test-results/screens/${tag}-handoff-preflight.png`, fullPage: true })

  // Start → fail-closed → calm Unavailable explainer (not an error spinner).
  await page.getByRole('button', { name: 'Start' }).click()
  await expect(page.getByText("Live hand-off isn't set up yet")).toBeVisible()
  await page.screenshot({
    path: `test-results/screens/${tag}-handoff-unavailable.png`,
    fullPage: true,
  })
})

test('live hand-off — WaitingForYou (demo canvas)', async ({ page }, testInfo) => {
  const tag = testInfo.project.name
  await signIn(page)

  // Client-side navigation to the demo route (keeps the in-memory session). The page
  // mounts fresh with ?demo=1 so [Start] mints a local demo handle.
  await page.evaluate(() => {
    window.history.pushState({}, '', '/handoff/conn-alice-health?demo=1')
    window.dispatchEvent(new PopStateEvent('popstate'))
  })

  await expect(page.getByText('This takes about 2 minutes.')).toBeVisible()
  await page.getByRole('button', { name: 'Start' }).click()

  // The target-identity strip + task list + trust line are the WaitingForYou anchors.
  await expect(page.getByText('portal.example-health.com')).toBeVisible()
  await expect(page.getByText('Waiting for you')).toBeVisible()
  await expect(page.getByText('We keep the session. We never see your password.')).toBeVisible()
  await page.screenshot({ path: `test-results/screens/${tag}-handoff-waiting.png`, fullPage: true })
})
