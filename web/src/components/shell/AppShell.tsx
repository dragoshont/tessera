import { useState, type ReactNode } from 'react'
import { NavLink } from 'react-router-dom'
import { Activity, Blocks, Bot, Brain, CalendarClock, LogOut, Menu, Radar, Settings2, Users, Wallet } from 'lucide-react'
import type { Person } from '../../data/types'
import { cn } from '../../lib/utils'
import { useSession } from '../../app/session'
import { Avatar } from '../ui/avatar'
import { Button } from '../ui/button'
import { Sheet, SheetContent, SheetTitle, SheetTrigger } from '../ui/sheet'
import { TesseraMark } from '../common/TesseraMark'
import { ThemeToggle } from '../theme/theme-toggle'

const navItemClass = ({ isActive }: { isActive: boolean }) =>
  cn(
    'flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-colors',
    isActive
      ? 'bg-muted text-foreground'
      : 'text-muted-foreground hover:bg-muted/60 hover:text-foreground',
  )

function SectionLabel({ children }: { children: ReactNode }) {
  return (
    <p className="px-3 pb-1 text-xs font-medium uppercase tracking-wide text-muted-foreground">
      {children}
    </p>
  )
}

export function SidebarNav({
  isAdmin,
  onNavigate,
}: {
  isAdmin: boolean
  onNavigate?: () => void
}) {
  return (
    <nav className="flex flex-col gap-6">
      <div className="flex flex-col gap-1">
        <SectionLabel>Mine</SectionLabel>
        <NavLink to="/chat" className={navItemClass} onClick={onNavigate}>
          <Bot className="h-4 w-4" aria-hidden />
          Chat
        </NavLink>
        <NavLink to="/jobs" className={navItemClass} onClick={onNavigate}>
          <CalendarClock className="h-4 w-4" aria-hidden />
          Jobs
        </NavLink>
        <NavLink to="/accounts" className={navItemClass} onClick={onNavigate}>
          <Wallet className="h-4 w-4" aria-hidden />
          Accounts
        </NavLink>
        <NavLink to="/activity" className={navItemClass} onClick={onNavigate}>
          <Activity className="h-4 w-4" aria-hidden />
          Activity &amp; access
        </NavLink>
        <NavLink to="/plugins" className={navItemClass} onClick={onNavigate}>
          <Blocks className="h-4 w-4" aria-hidden />
          Plugins
        </NavLink>
        <NavLink to="/memory" className={navItemClass} onClick={onNavigate}>
          <Brain className="h-4 w-4" aria-hidden />
          Memory
        </NavLink>
        <NavLink to="/settings" className={navItemClass} onClick={onNavigate}>
          <Settings2 className="h-4 w-4" aria-hidden />
          Settings
        </NavLink>
      </div>

      {isAdmin ? (
        <div className="flex flex-col gap-1">
          <SectionLabel>Admin</SectionLabel>
          <NavLink to="/settings/admin/users" className={navItemClass} onClick={onNavigate}>
            <Users className="h-4 w-4" aria-hidden />
            Users
          </NavLink>
          <NavLink to="/settings/admin/observability" className={navItemClass} onClick={onNavigate}>
            <Radar className="h-4 w-4" aria-hidden />
            Observability
          </NavLink>
        </div>
      ) : null}
    </nav>
  )
}

function SidebarHeader() {
  return (
    <div className="flex items-center gap-2.5 border-b border-border px-5 py-4">
      <TesseraMark className="h-8 w-8 text-accent" />
      <div className="leading-tight">
        <div className="text-sm font-semibold">Tessera</div>
        <div className="text-xs text-muted-foreground">homelab</div>
      </div>
    </div>
  )
}

function SidebarFooter({ user, onSignOut }: { user: Person | null; onSignOut: () => void }) {
  return (
    <div className="border-t border-border p-3">
      <div className="flex items-center gap-2 rounded-lg px-2 py-2">
        <Avatar name={user?.principal ?? '?'} />
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-1 text-sm font-medium">
            <span className="truncate">{user?.principal ?? 'Signed out'}</span>
            {user ? (
              <span className="shrink-0 text-xs font-normal text-muted-foreground">(you)</span>
            ) : null}
          </div>
          <div className="text-xs text-muted-foreground">{user?.role ?? ''}</div>
        </div>
      </div>
      <div className="mt-1 flex items-center justify-between gap-2">
        <span className="hidden md:block">
          <ThemeToggle />
        </span>
        <Button variant="ghost" size="sm" onClick={onSignOut}>
          <LogOut className="h-4 w-4" aria-hidden />
          Sign out
        </Button>
      </div>
    </div>
  )
}

export function AppShell({ children }: { children: ReactNode }) {
  const { currentUser, signOut } = useSession()
  const [mobileOpen, setMobileOpen] = useState(false)
  const isAdmin = currentUser?.role === 'Admin'

  return (
    <div className="min-h-screen bg-surface">
      <aside className="fixed inset-y-0 left-0 z-20 hidden w-64 flex-col border-r border-border bg-card md:flex">
        <SidebarHeader />
        <div className="flex-1 overflow-y-auto p-3">
          <SidebarNav isAdmin={isAdmin} />
        </div>
        <SidebarFooter user={currentUser} onSignOut={signOut} />
      </aside>

      <header className="sticky top-0 z-30 flex items-center justify-between border-b border-border bg-card/80 px-4 py-3 backdrop-blur md:hidden">
        <div className="flex items-center gap-2">
          <TesseraMark className="h-7 w-7 text-accent" />
          <span className="font-semibold">Tessera</span>
        </div>
        <div className="flex items-center gap-1">
          <ThemeToggle />
          <Sheet open={mobileOpen} onOpenChange={setMobileOpen}>
            <SheetTrigger asChild>
              <Button variant="ghost" size="icon" aria-label="Open navigation">
                <Menu className="h-5 w-5" />
              </Button>
            </SheetTrigger>
            <SheetContent side="left" className="p-0" aria-describedby={undefined}>
              <SheetTitle className="sr-only">Navigation</SheetTitle>
              <SidebarHeader />
              <div className="flex-1 overflow-y-auto p-3">
                <SidebarNav isAdmin={isAdmin} onNavigate={() => setMobileOpen(false)} />
              </div>
              <SidebarFooter user={currentUser} onSignOut={signOut} />
            </SheetContent>
          </Sheet>
        </div>
      </header>

      <main className="md:pl-64">
        <div className="mx-auto w-full max-w-5xl px-4 py-6 sm:px-6 lg:px-8 lg:py-10">{children}</div>
      </main>
    </div>
  )
}
