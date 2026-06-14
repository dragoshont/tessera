import type { ReactNode } from 'react'
import { useActivity, useCurrentUser, useDelegations, useModules } from '../api/hooks'
import { ActivityFeed } from '../components/activity/ActivityFeed'
import { DelegationsCard } from '../components/access/DelegationsCard'
import { ModulesCard } from '../components/modules/ModulesCard'

function Section({
  title,
  description,
  children,
}: {
  title: string
  description: string
  children: ReactNode
}) {
  return (
    <section className="space-y-3">
      <div>
        <h2 className="text-base font-semibold">{title}</h2>
        <p className="text-sm text-muted-foreground">{description}</p>
      </div>
      {children}
    </section>
  )
}

/**
 * "Activity & access" — the self surface of the awareness dashboard (ADR 0017).
 * Everything is scoped to the signed-in person (even an operator sees only their
 * own here; the cross-person view is the separate Observability route). Read-only,
 * secret-free.
 */
export function ActivityAccessPage() {
  const { data: user } = useCurrentUser()
  const principal = user?.principal
  // Scope to self explicitly so an operator's self page shows their own data, not
  // everyone's (the operator's all-view lives on /admin/observability).
  const { data: modules, isLoading: modulesLoading } = useModules()
  const { data: delegations, isLoading: delegationsLoading } = useDelegations(principal)
  const { data: feed, isLoading: feedLoading } = useActivity(principal)

  return (
    <div className="mx-auto max-w-3xl space-y-8">
      <header>
        <h1 className="text-xl font-semibold">Activity &amp; access</h1>
        <p className="text-sm text-muted-foreground">
          What can act on your behalf, what is connected, and what has happened — never a secret value.
        </p>
      </header>

      <Section
        title="Who can act as you"
        description="Every agent or automation granted access on your behalf, and what needs your confirmation."
      >
        <DelegationsCard delegations={delegations} isLoading={delegationsLoading || !principal} />
      </Section>

      <Section title="Loaded modules" description="The connectors the broker has available and what they can do.">
        <ModulesCard modules={modules} isLoading={modulesLoading} />
      </Section>

      <Section title="Your activity" description="A secret-free log of decisions made on your behalf.">
        <ActivityFeed
          feed={feed}
          isLoading={feedLoading || !principal}
          emptyHint="No activity on your behalf yet."
        />
      </Section>
    </div>
  )
}
