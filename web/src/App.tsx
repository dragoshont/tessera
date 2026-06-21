import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter, Navigate, Outlet, Route, Routes } from 'react-router-dom'
import { TesseraClientProvider } from './api/hooks'
import { SessionProvider, useSession } from './app/session'
import { AppShell } from './components/shell/AppShell'
import { TesseraMark } from './components/common/TesseraMark'
import { ThemeProvider } from './components/theme/theme-provider'
import { ToastProvider } from './components/ui/toast'
import { AccountsPage } from './pages/AccountsPage'
import { ActionRequiredPage } from './pages/ActionRequiredPage'
import { ActivityAccessPage } from './pages/ActivityAccessPage'
import { AllConnectionsPage } from './pages/AllConnectionsPage'
import { AuthCallbackPage } from './pages/AuthCallbackPage'
import { ConnectWizardPage } from './pages/ConnectWizardPage'
import { LiveHandoffPage } from './pages/LiveHandoffPage'
import { ObservabilityPage } from './pages/ObservabilityPage'
import { PendingWritesPage } from './pages/PendingWritesPage'
import { PersonDetailPage } from './pages/PersonDetailPage'
import { SignInPage } from './pages/SignInPage'
import { UsersPage } from './pages/UsersPage'

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
  if (status === 'anonymous') return <Navigate to="/sign-in" replace />
  return <Outlet />
}

function RootLayout() {
  return (
    <AppShell>
      <Outlet />
    </AppShell>
  )
}

function AppRoutes() {
  return (
    <Routes>
      <Route path="/sign-in" element={<SignInPage />} />
      <Route path="/auth/callback" element={<AuthCallbackPage />} />
      <Route element={<RequireAuth />}>
        <Route element={<RootLayout />}>
          <Route path="/" element={<Navigate to="/accounts" replace />} />
          <Route path="/accounts" element={<AccountsPage />} />
          <Route path="/accounts/:connectionId" element={<AccountsPage />} />
          <Route path="/activity" element={<ActivityAccessPage />} />
          <Route path="/pending-writes" element={<PendingWritesPage />} />
          <Route path="/connect" element={<ConnectWizardPage />} />
          <Route path="/handoff/:connectionId" element={<LiveHandoffPage />} />
          <Route path="/action-required" element={<ActionRequiredPage />} />
          <Route path="/admin/users" element={<UsersPage />} />
          <Route path="/admin/users/:principal" element={<PersonDetailPage />} />
          <Route path="/admin/connections" element={<AllConnectionsPage />} />
          <Route path="/admin/observability" element={<ObservabilityPage />} />
        </Route>
      </Route>
      <Route path="*" element={<Navigate to="/accounts" replace />} />
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
                <AppRoutes />
              </BrowserRouter>
            </SessionProvider>
          </ToastProvider>
        </TesseraClientProvider>
      </QueryClientProvider>
    </ThemeProvider>
  )
}
