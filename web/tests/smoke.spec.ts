import { expect, test } from '@playwright/test'

async function signIn(page: import('@playwright/test').Page) {
  await page.goto('/')
  // Demo/dev loopback: the developer card signs in as a chosen principal.
  await page.getByLabel('Developer sign-in (local only)').fill('alice@example.com')
  await page.getByRole('button', { name: /continue/i }).click()
  await expect(page).toHaveURL(/\/accounts$/)
}

async function openNav(page: import('@playwright/test').Page) {
  // The shell must be painted before we probe the (phone-only) hamburger.
  await expect(page.getByRole('heading', { name: 'My accounts' })).toBeVisible()
  const menuButton = page.getByRole('button', { name: 'Open navigation' })
  if (await menuButton.isVisible()) {
    await menuButton.click()
  }
}

test('sign in, read My accounts, open a connection — and reveal no secret', async ({ page }) => {
  await signIn(page)

  await expect(page.getByRole('heading', { name: 'My accounts' })).toBeVisible()
  await expect(page.getByText('acting as alice@example.com')).toBeVisible()

  // All four health states are present at a glance.
  await expect(page.getByText('Live', { exact: true }).first()).toBeVisible()
  await expect(page.getByText('Expiring soon')).toBeVisible()
  await expect(page.getByText('Absent')).toBeVisible()
  await expect(page.getByText('Error', { exact: true }).first()).toBeVisible()

  // Open the detail drawer.
  await page.getByText('Health Portal').first().click()
  await expect(page.getByText("Tessera can't show this — that's the point.")).toBeVisible()
  await expect(page.getByText(/has cookies/i)).toBeVisible()

  // The crux: no reveal / copy affordance anywhere.
  await expect(page.getByRole('button', { name: /reveal|show secret|show value|copy|unmask/i })).toHaveCount(0)
  await expect(page.locator('input[type="password"]')).toHaveCount(0)
})

test('Users view shows the operator as Admin and the others as Members', async ({ page }) => {
  await signIn(page)
  await openNav(page)
  await page.getByRole('link', { name: 'Users' }).click()

  await expect(page.getByRole('heading', { name: 'Users' })).toBeVisible()

  const aliceRow = page.getByRole('row', { name: /alice@example\.com/ })
  await expect(aliceRow.getByText('Admin')).toBeVisible()

  const bobRow = page.getByRole('row', { name: /bob@example\.com/ })
  await expect(bobRow.getByText('Member')).toBeVisible()

  const carolRow = page.getByRole('row', { name: /carol@example\.com/ })
  await expect(carolRow.getByText('Member')).toBeVisible()
})
