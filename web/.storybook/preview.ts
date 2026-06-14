import type { Decorator, Preview } from '@storybook/react-vite'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { createElement } from 'react'
import '../src/index.css'

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: false,
      refetchOnWindowFocus: false,
    },
  },
})

// One decorator gives every story the providers it might touch (query client +
// a router for any component that renders links) and a themed surface so light
// and dark screenshots both look correct.
const withProviders: Decorator = (Story, context) => {
  const theme = context.globals.theme === 'dark' ? 'dark' : ''
  return createElement(
    'div',
    { className: theme },
    createElement(
      'div',
      { className: 'min-h-screen bg-surface text-foreground' },
      createElement(
        QueryClientProvider,
        { client: queryClient },
        createElement(MemoryRouter, null, createElement(Story)),
      ),
    ),
  )
}

const preview: Preview = {
  decorators: [withProviders],
  globalTypes: {
    theme: {
      description: 'Light / dark surface',
      defaultValue: 'light',
      toolbar: {
        title: 'Theme',
        icon: 'mirror',
        items: [
          { value: 'light', title: 'Light' },
          { value: 'dark', title: 'Dark' },
        ],
        dynamicTitle: true,
      },
    },
  },
  parameters: {
    layout: 'fullscreen',
    a11y: { test: 'todo' },
  },
}

export default preview
