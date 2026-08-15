import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query'
import {
  BrowserRouter,
  Navigate,
  Outlet,
  Route,
  Routes,
  useNavigate,
} from 'react-router-dom'
import { useEffect, useRef } from 'react'
import { TesseraClientProvider } from './api/hooks'
import { SessionProvider, useSession } from './app/session'
import { AppShell } from './components/shell/AppShell'
import { TesseraMark } from './components/common/TesseraMark'
import { ThemeProvider } from './components/theme/theme-provider'
import { ToastProvider } from './components/ui/toast'
import { AccountsPage as LegacyAccountsPage } from './pages/AccountsPage'
import { ActionRequiredPage } from './pages/ActionRequiredPage'
import { ActivityAccessPage } from './pages/ActivityAccessPage'
import { AuthCallbackPage } from './pages/AuthCallbackPage'
import { ConnectWizardPage } from './pages/ConnectWizardPage'
import { ContinuityPage } from './pages/ContinuityPage'
import { LiveHandoffPage } from './pages/LiveHandoffPage'
import { ObservabilityPage } from './pages/ObservabilityPage'
import { PendingWritesPage } from './pages/PendingWritesPage'
import { SignInPage } from './pages/SignInPage'
import { UsersPage } from './pages/UsersPage'
import { ChatPage } from './pages/ChatPage'
import { RemoteHostPage, RemoteHostsPage } from './pages/RemoteHostsPage'
import { ModelDefaultsPanel } from './components/product/ModelDefaultsPanel'
import { JobAccessPanel } from './components/product/JobAccessPanel'
import { AccountsPage, ActivityPage, JobsPage, MemoryPage, PluginsPage, SettingsPage } from './pages/R2ProductPages'
import { subscribeDesktopNavigation } from './app/runtime'
import { isDesktop, notifyDesktop } from './app/runtime'
import { r2Api } from './api/r2'

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      refetchOnWindowFocus: false,
      retry: false,
    },
  },
})

function BootSplash() {
  // Brief, calm bootstrap while /portal/config + /portal/me resolve — not an
  // infinite spinner; it resolves to either the shell or the sign-in screen.
  return (
    <div className="flex min-h-screen items-center justify-center bg-surface" aria-live="polite">
      <div className="flex flex-col items-center gap-3 text-muted-foreground">
        <TesseraMark className="h-10 w-10 animate-pulse text-accent" />
        <p className="text-sm">Checking your session…</p>
      </div>
    </div>
  )
}

function RequireAuth() {
  const { status } = useSession()
  if (status === 'loading') return <BootSplash />
  if (status === 'unavailable') {
    return (
      <div className="flex min-h-screen items-center justify-center bg-surface px-6">
        <div className="max-w-md text-center">
          <TesseraMark className="mx-auto h-10 w-10 text-muted-foreground" />
          <h1 className="mt-4 text-xl font-semibold">Tessera is offline</h1>
          <p className="mt-2 text-sm text-muted-foreground">
            The deployed Tessera service cannot be reached. Canonical state remains on the server;
            this app will not create a separate local copy.
          </p>
          <button className="mt-5 text-sm font-medium text-accent" onClick={() => window.location.reload()}>
            Try again
          </button>
        </div>
      </div>
    )
  }
  if (status === 'anonymous') return <Navigate to="/sign-in" replace />
  return <Outlet />
}

function DesktopNavigation() {
  const navigate = useNavigate()
  useEffect(() => subscribeDesktopNavigation((route) => navigate(route)), [navigate])
  return null
}

function DesktopNotifications() {
  const { status } = useSession()
  const enabled = isDesktop() && status === 'authenticated'
  const actions = useQuery({
    queryKey: ['desktop', 'pending-actions'],
    queryFn: () => r2Api.actions('?approvalRequired=true'),
    enabled,
    refetchInterval: 30_000,
  })
  const jobs = useQuery({
    queryKey: ['desktop', 'jobs'],
    queryFn: r2Api.jobs,
    enabled,
    refetchInterval: 30_000,
  })
  const accounts = useQuery({
    queryKey: ['desktop', 'accounts'],
    queryFn: r2Api.accounts,
    enabled,
    refetchInterval: 30_000,
  })
  const seen = useRef(new Set<string>())

  useEffect(() => {
    for (const action of actions.data?.items ?? []) {
      const key = `action:${action.id}:${action.version}`
      if (seen.current.has(key) || action.state !== 'PROPOSED') continue
      seen.current.add(key)
      void notifyDesktop({
        title: 'Action approval pending',
        body: `${action.capabilityId} is waiting for your review.`,
        route: '/activity',
      })
    }
  }, [actions.data])

  useEffect(() => {
    for (const job of jobs.data?.items ?? []) {
      const run = job.lastRun
      if (!run || (run.state !== 'FAILED' && run.state !== 'SUCCEEDED')) continue
      const key = `job:${run.id}:${run.version}`
      if (seen.current.has(key)) continue
      seen.current.add(key)
      void notifyDesktop({
        title: run.state === 'FAILED' ? 'Job failed' : 'Job completed',
        body: job.name,
        route: '/jobs',
      })
    }
  }, [jobs.data])

  useEffect(() => {
    for (const account of accounts.data?.items ?? []) {
      if (account.lifecycle !== 'AUTH_REQUIRED') continue
      const key = `account:${account.id}:${account.version}`
      if (seen.current.has(key)) continue
      seen.current.add(key)
      void notifyDesktop({
        title: 'Account needs authorization',
        body: account.displayName,
        route: '/accounts',
      })
    }
  }, [accounts.data])

  return null
}

function RootLayout() {
  return (
    <AppShell>
      <Outlet />
    </AppShell>
  )
}

function ContinuityRoute() {
  return import.meta.env.MODE === 'e2e' && navigator.webdriver ? <ContinuityPage /> : <Navigate to="/chat" replace />
}

function AppRoutes() {
  return (
    <Routes>
      <Route path="/sign-in" element={<SignInPage />} />
      <Route path="/auth/callback" element={<AuthCallbackPage />} />
      <Route element={<RequireAuth />}>
        <Route element={<RootLayout />}>
          <Route path="/" element={<Navigate to="/chat" replace />} />
          <Route path="/chat" element={<ChatPage />} />
          <Route path="/jobs" element={<><JobsPage /><JobAccessPanel /></>} />
          <Route path="/plugins" element={<PluginsPage />} />
          <Route path="/memory" element={<MemoryPage />} />
          <Route path="/settings" element={<><SettingsPage /><ModelDefaultsPanel /></>} />
          <Route path="/accounts" element={<AccountsPage />} />
          <Route path="/accounts/:connectionId" element={<LegacyAccountsPage />} />
          <Route path="/activity" element={<ActivityPage />} />
          <Route path="/remote" element={<RemoteHostsPage />} />
          <Route path="/remote/hosts/:hostId" element={<RemoteHostPage />} />
          <Route path="/connect" element={<Navigate to="/accounts" replace />} />
          <Route path="/continuity" element={<ContinuityRoute />} />
          <Route path="/pending-writes" element={<Navigate to="/activity" replace />} />
          <Route path="/action-required" element={<Navigate to="/activity" replace />} />
          <Route path="/handoff/:connectionId" element={<LiveHandoffPage />} />
          <Route path="/settings/admin/users" element={<UsersPage />} />
          <Route path="/settings/admin/legacy-accounts" element={<LegacyAccountsPage />} />
          <Route path="/settings/admin/connections/:connectionId" element={<LegacyAccountsPage />} />
          <Route path="/settings/admin/activity" element={<ActivityAccessPage />} />
          <Route path="/settings/admin/pending-writes" element={<PendingWritesPage />} />
          <Route path="/settings/admin/action-required" element={<ActionRequiredPage />} />
          <Route path="/settings/admin/connect" element={<ConnectWizardPage />} />
          <Route path="/settings/admin/observability" element={<ObservabilityPage />} />
          <Route path="/admin/users" element={<Navigate to="/settings/admin/users" replace />} />
          <Route path="/admin/observability" element={<Navigate to="/settings/admin/observability" replace />} />
        </Route>
      </Route>
      <Route path="*" element={<Navigate to="/chat" replace />} />
    </Routes>
  )
}

export function App() {
  // Compose the router with Vite's base path so a GitHub Pages build under
  // '/tessera/' routes correctly; '/' (the homelab default) yields a root basename.
  const basename = import.meta.env.BASE_URL.replace(/\/$/, '') || '/'
  return (
    <ThemeProvider>
      <QueryClientProvider client={queryClient}>
        <TesseraClientProvider>
          <ToastProvider>
            <SessionProvider>
              <BrowserRouter basename={basename}>
                <DesktopNavigation />
                <DesktopNotifications />
                <AppRoutes />
              </BrowserRouter>
            </SessionProvider>
          </ToastProvider>
        </TesseraClientProvider>
      </QueryClientProvider>
    </ThemeProvider>
  )
}
