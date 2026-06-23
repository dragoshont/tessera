import { expect, test } from '@playwright/test'

// ADR 0025 / SDD-01 — the awareness/accounts view must show HONEST health: a
// present-but-unverified connection renders "Unverified" (amber caution), NEVER an
// optimistic green "Live", and the page must not crash on the new status (the
// fail-closed contract the adversarial judge flagged). Run: npx playwright test health
test('accounts view shows honest health — Unverified, never a false Live', async ({
  page,
}, testInfo) => {
  const pageErrors: string[] = []
  page.on('pageerror', (e) => pageErrors.push(String(e)))

  // demo/dev loopback sign-in (same entry as handoff.spec)
  await page.goto('/')
  await page.getByLabel('Developer sign-in (local only)').fill('alice@example.com')
  await page.getByRole('button', { name: /continue/i }).click()
  await expect(page).toHaveURL(/\/accounts$/)

  // The honest state is shown: present-but-unverified -> amber "Unverified".
  await expect(page.getByLabel('Health: Unverified').first()).toBeVisible()
  // The false-green is gone: no connection in this view renders as "Live".
  await expect(page.getByLabel('Health: Live')).toHaveCount(0)
  // The new status did not crash the portal (the fail-closed contract fix).
  expect(pageErrors, pageErrors.join('\n')).toHaveLength(0)

  await page.screenshot({
    path: `test-results/health-honesty-${testInfo.project.name}.png`,
    fullPage: true,
  })
})
