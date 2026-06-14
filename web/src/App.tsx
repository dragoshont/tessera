import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter, Navigate, Outlet, Route, Routes } from 'react-router-dom'
import { TesseraClientProvider } from './api/hooks'
import { SessionProvider, useSession } from './app/session'
import { AppShell } from './components/shell/AppShell'
import { ThemeProvider } from './components/theme/theme-provider'
import { AccountsPage } from './pages/AccountsPage'
import { ActionRequiredPage } from './pages/ActionRequiredPage'
import { AllConnectionsPage } from './pages/AllConnectionsPage'
import { ConnectWizardPage } from './pages/ConnectWizardPage'
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

function RequireAuth() {
  const { currentUser } = useSession()
  if (!currentUser) return <Navigate to="/sign-in" replace />
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
      <Route element={<RequireAuth />}>
        <Route element={<RootLayout />}>
          <Route path="/" element={<Navigate to="/accounts" replace />} />
          <Route path="/accounts" element={<AccountsPage />} />
          <Route path="/accounts/:connectionId" element={<AccountsPage />} />
          <Route path="/connect" element={<ConnectWizardPage />} />
          <Route path="/action-required" element={<ActionRequiredPage />} />
          <Route path="/admin/users" element={<UsersPage />} />
          <Route path="/admin/users/:principal" element={<PersonDetailPage />} />
          <Route path="/admin/connections" element={<AllConnectionsPage />} />
        </Route>
      </Route>
      <Route path="*" element={<Navigate to="/accounts" replace />} />
    </Routes>
  )
}

export function App() {
  return (
    <ThemeProvider>
      <QueryClientProvider client={queryClient}>
        <TesseraClientProvider>
          <SessionProvider>
            <BrowserRouter>
              <AppRoutes />
            </BrowserRouter>
          </SessionProvider>
        </TesseraClientProvider>
      </QueryClientProvider>
    </ThemeProvider>
  )
}
