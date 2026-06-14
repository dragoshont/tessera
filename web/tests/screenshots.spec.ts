import { expect, test, type Page } from '@playwright/test'

// Captures the Phase 0 screens. Run with:
//   npx playwright test screenshots            (after `npx playwright install chromium`)
// Each Playwright project (desktop 1280 / phone 390) produces its own set, and
// every screen is captured in both light and dark.

async function signIn(page: Page) {
  await page.goto('/')
  await page.getByLabel('Developer sign-in (local only)').fill('alice@example.com')
  await page.getByRole('button', { name: /continue/i }).click()
  await expect(page).toHaveURL(/\/accounts$/)
}

async function setTheme(page: Page, theme: 'light' | 'dark') {
  await page.evaluate((value) => window.localStorage.setItem('tessera.theme', value), theme)
}

for (const theme of ['light', 'dark'] as const) {
  test(`screens — ${theme}`, async ({ page }, testInfo) => {
    const tag = `${testInfo.project.name}-${theme}`

    // Sign-in (set theme before first paint).
    await page.goto('/')
    await setTheme(page, theme)
    await page.goto('/sign-in')
    await expect(page.getByText('Developer sign-in (local only)')).toBeVisible()
    await page.screenshot({ path: `test-results/screens/${tag}-sign-in.png`, fullPage: true })

    // My accounts (mixed health).
    await signIn(page)
    await expect(page.getByRole('heading', { name: 'My accounts' })).toBeVisible()
    await page.screenshot({ path: `test-results/screens/${tag}-accounts.png`, fullPage: true })

    // Connection drawer (presence flags + never-reveal line).
    await page.getByText('Health Portal').first().click()
    await expect(page.getByText("Tessera can't show this — that's the point.")).toBeVisible()
    await page.screenshot({ path: `test-results/screens/${tag}-connection-drawer.png`, fullPage: true })
    await page.keyboard.press('Escape')
    await expect(page.getByText("Tessera can't show this — that's the point.")).toBeHidden()

    // Users (admin). On phone the nav lives behind the hamburger.
    if (testInfo.project.name === 'phone') {
      await page.getByRole('button', { name: 'Open navigation' }).click()
      await expect(page.getByRole('link', { name: 'Users' })).toBeVisible()
    }
    await page.getByRole('link', { name: 'Users' }).click()
    await expect(page.getByRole('heading', { name: 'Users' })).toBeVisible()
    await page.screenshot({ path: `test-results/screens/${tag}-users.png`, fullPage: true })

    // Connect wizard — provider picker (step 1).
    await page.goto('/connect')
    await expect(page.getByRole('heading', { name: 'Which account?' })).toBeVisible()
    await page.screenshot({ path: `test-results/screens/${tag}-connect-provider.png`, fullPage: true })
  })
}
