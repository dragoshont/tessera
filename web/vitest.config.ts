import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'

// Test runner config is kept separate from the Vite build config so unit tests
// do not pull in the Tailwind pipeline (behavior is tested, not visual styling).
export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    globals: false,
    css: false,
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
  },
})
