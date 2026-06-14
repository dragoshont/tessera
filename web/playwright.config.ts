import { defineConfig, devices } from '@playwright/test'

// Phase 0 smoke + screenshot checks. Runs the Vite dev server, signs in through
// the static Microsoft button, and verifies the two views plus the never-reveal
// invariant. Capture happens at desktop (1280) and phone (390).
export default defineConfig({
  testDir: './tests',
  timeout: 30_000,
  expect: { timeout: 5_000 },
  outputDir: './test-results',
  webServer: {
    command: 'npm run dev -- --host 127.0.0.1 --port 4178',
    url: 'http://127.0.0.1:4178',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
  projects: [
    {
      name: 'desktop',
      use: { ...devices['Desktop Chrome'], baseURL: 'http://127.0.0.1:4178', viewport: { width: 1280, height: 1000 } },
    },
    {
      name: 'phone',
      use: { ...devices['Desktop Chrome'], baseURL: 'http://127.0.0.1:4178', isMobile: true, hasTouch: true, viewport: { width: 390, height: 900 } },
    },
  ],
})
