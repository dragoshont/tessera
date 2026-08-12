import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import { App } from './App'
import { hydrateAuthState } from './app/auth'
import { initializeRuntime } from './app/runtime'

void initializeRuntime()
  .then((auth) => {
    if (auth !== undefined) hydrateAuthState(auth)
    createRoot(document.getElementById('root')!).render(
      <StrictMode>
        <App />
      </StrictMode>,
    )
  })
  .catch(() => {
    const root = document.getElementById('root')
    if (root) root.textContent = 'Tessera Desktop could not initialize its secure runtime.'
  })
