/* eslint-disable react-refresh/only-export-components */
import type { Decorator } from '@storybook/react-vite'
import { useEffect, type ReactNode } from 'react'

function DarkRemoteStory({ children }: { children: ReactNode }) {
  useEffect(() => {
    document.documentElement.classList.add('dark')
    return () => document.documentElement.classList.remove('dark')
  }, [])

  return <div className="min-h-screen bg-surface p-4 text-foreground">{children}</div>
}

export const withDarkRemote: Decorator = (Story) => <DarkRemoteStory><Story /></DarkRemoteStory>