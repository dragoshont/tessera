import { expect, test, type Page } from '@playwright/test'

async function signIn(page: Page) {
  await page.goto('/')
  await page.getByLabel('Developer sign-in (local only)').fill('alice@example.com')
  await page.getByRole('button', { name: /continue/i }).click()
  await expect(page).toHaveURL(/\/chat$/)
  await page.goto('/continuity')
  await expect(page.getByRole('heading', { name: 'Continuity' })).toBeVisible()
}

test('continuity compounds accepted history through correction, conflict, and completion', async ({ page }, testInfo) => {
  await page.emulateMedia({ reducedMotion: 'reduce' })
  await signIn(page)

  await expect(page.getByText('Uses synthetic local evidence; no provider is connected.')).toBeVisible()
  await expect(page.getByText(/changes are held in this browser session, disappear on reload/i)).toBeVisible()
  await page.getByRole('button', { name: 'Track example follow-up' }).click()
  const detail = page.getByRole('dialog')
  await expect(detail).toBeVisible()
  await expect(detail).toHaveCSS('transition-property', 'none')

  await detail.getByRole('button', { name: 'Accept candidate' }).click()
  await expect(detail.getByRole('button', { name: 'Observe schedule update' })).toBeVisible()

  await detail.getByRole('button', { name: 'Correct' }).click()
  const correction = page.getByRole('dialog', { name: 'Correct follow-up' })
  await expect(correction).toHaveCSS('transition-property', 'none')
  await correction.getByLabel('New value').fill('lease renewal checklist')
  await correction.getByRole('button', { name: 'Save correction' }).click()
  await expect(correction).toBeHidden()
  await expect(detail.getByRole('heading', { name: 'lease renewal checklist' })).toBeVisible()

  await detail.getByRole('button', { name: 'Observe schedule update' }).click()
  await expect(detail.getByText('2026-08-17')).toBeVisible()
  await detail.getByRole('button', { name: 'Accept candidate' }).click()
  await expect(detail.getByRole('button', { name: 'Observe conflicting update' })).toBeVisible()

  await detail.getByRole('button', { name: 'Observe conflicting update' }).click()
  await expect(detail.getByText('Conflict', { exact: true })).toBeVisible()
  await detail.getByRole('button', { name: 'Resolve conflict' }).click()
  const resolution = page.getByRole('dialog', { name: 'Resolve conflict' })
  await resolution.getByLabel('New value').fill('2026-08-17')
  await resolution.getByRole('button', { name: 'Resolve' }).click()
  await expect(resolution).toBeHidden()
  await expect(detail.getByRole('button', { name: 'Observe completion update' })).toBeVisible()

  await detail.getByRole('button', { name: 'Observe completion update' }).click()
  await detail.getByRole('button', { name: 'Accept candidate' }).click()
  await expect(detail.locator('[data-continuity-state="completed"]')).toBeVisible()

  await detail.getByRole('tab', { name: 'Timeline' }).click()
  for (const summary of [
    'Corrected deliverable.',
    'Observed a schedule update resolved from accepted context.',
    'Detected incompatible due-date evidence.',
    'Resolved dueAt conflict.',
    'Observed completion resolved from accepted context.',
  ]) {
    await expect(detail.getByText(summary, { exact: true })).toBeVisible()
  }

  await detail.getByRole('tab', { name: 'Why' }).click()
  const mondayWhy = detail.locator('[data-revision-id="revision:r1-monday:dueAt"]')
  await expect(mondayWhy.getByText('evidence:local.fixture:r1-monday', { exact: true })).toBeVisible()
  await expect(mondayWhy.getByText('revision:r1-initial:counterparty', { exact: true })).toBeVisible()
  await expect(mondayWhy.getByText('revision:r1-initial:dueAt', { exact: true })).toBeVisible()
  const correctedRevision = (await mondayWhy.locator('li').filter({ hasText: /^revision:correct-/ }).textContent())?.trim()
  expect(correctedRevision).toMatch(/^revision:correct-.*:deliverable$/)

  const conflictWhy = detail.locator('[data-revision-id="revision:r1-conflicting-friday:dueAt"]')
  await expect(conflictWhy.getByText('evidence:local.fixture:r1-conflicting-friday', { exact: true })).toBeVisible()
  await expect(conflictWhy.getByText('revision:r1-monday:dueAt', { exact: true })).toBeVisible()

  const resolutionWhy = detail.locator('[data-follow-up-field="dueAt"]')
    .filter({ hasText: 'Current through explicit user correction.' })
  await expect(resolutionWhy.getByText(/^evidence:user\.resolution:resolve-/)).toBeVisible()
  await expect(resolutionWhy.getByText('revision:r1-monday:dueAt', { exact: true })).toBeVisible()
  await expect(resolutionWhy.getByText('revision:r1-conflicting-friday:dueAt', { exact: true })).toBeVisible()

  const completionWhy = detail.locator('[data-revision-id="revision:r1-sent:completedAt"]')
  await expect(completionWhy.getByText('evidence:local.fixture:r1-sent', { exact: true })).toBeVisible()
  await expect(completionWhy.getByText(correctedRevision as string, { exact: true })).toBeVisible()
  await expect(completionWhy.getByText('revision:r1-initial:counterparty', { exact: true })).toBeVisible()

  await page.screenshot({
    path: `test-results/screens/${testInfo.project.name}-continuity-completed.png`,
    fullPage: true,
  })

  await detail.getByRole('button', { name: 'Close' }).click()
  await page.getByRole('tab', { name: 'Tracked' }).click()
  let opener
  if (testInfo.project.name === 'phone') {
    const list = page.getByRole('list', { name: 'Follow-ups' })
    await expect(list).toBeVisible()
    await expect(list.getByText('lease renewal checklist')).toBeVisible()
    await expect(page.getByRole('table')).toBeHidden()
    opener = list.getByRole('button', { name: 'Open detail' })
  } else {
    const table = page.getByRole('table')
    await expect(table).toBeVisible()
    await expect(table.getByRole('cell', { name: 'lease renewal checklist', exact: true })).toBeVisible()
    await expect(page.getByRole('list', { name: 'Follow-ups' })).toBeHidden()
    opener = table.getByRole('button', { name: 'Open lease renewal checklist' })
  }

  await opener.click()
  await page.keyboard.press('Escape')
  await expect(detail).toBeHidden()
  await expect(opener).toBeFocused()
})
