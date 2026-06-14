import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { defineConfig, globalIgnores } from 'eslint/config'

export default defineConfig([
  globalIgnores(['dist', 'storybook-static', 'test-results', 'playwright-report', 'coverage']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommended,
      reactHooks.configs.flat.recommended,
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      globals: globals.browser,
    },
    rules: {
      // TanStack Table's useReactTable returns functions the React Compiler lint
      // rule can't memoize; the pattern is known-safe and we don't ship the
      // compiler, so this noise is silenced.
      'react-hooks/incompatible-library': 'off',
    },
  },
  {
    // shadcn-style primitives and the context providers intentionally co-export
    // variants / hooks / re-exported Radix primitives next to their components.
    files: [
      'src/components/ui/**/*.tsx',
      'src/components/theme/theme-provider.tsx',
      'src/api/hooks.tsx',
      'src/app/session.tsx',
    ],
    rules: {
      'react-refresh/only-export-components': 'off',
    },
  },
])
