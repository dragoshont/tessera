import { readFile } from 'node:fs/promises'
import path from 'node:path'
import { describe, expect, it } from 'vitest'
import { PRODUCT_ROUTES } from '../src/security'

const required = ['/chat', '/jobs', '/accounts', '/plugins', '/memory', '/activity', '/settings']

describe('shared product route parity', () => {
  it('keeps every required Web route available to Desktop navigation', async () => {
    const app = await readFile(path.resolve(__dirname, '../../web/src/App.tsx'), 'utf8')
    for (const route of required) {
      expect(app).toContain(`path="${route}"`)
      expect(PRODUCT_ROUTES.has(route)).toBe(true)
    }
  })

  it('does not introduce a desktop scheduler, memory store, or provider client', async () => {
    const main = await readFile(path.resolve(__dirname, '../src/main.ts'), 'utf8')
    expect(main).not.toMatch(/gmail|regina.?maria|scheduler|sqlite/i)
    expect(main).toContain('API_ORIGIN')
  })
})
