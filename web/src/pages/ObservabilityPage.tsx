import type { ReactNode } from 'react'
import { useActivity, useDelegations, useModules } from '../api/hooks'
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
 * "Observability" — the operator surface of the awareness dashboard (ADR 0017).
 * Cross-person: who is using auth (every delegation, including automation), what
 * modules are loaded, and the full secret-free activity feed across everyone.
 * Operator-only route (the sidebar shows it to admins); the broker also enforces
 * the cross-person scope server-side.
 */
export function ObservabilityPage() {
  // No principal → the operator's all-scope views (everyone's activity + grants).
  const { data: feed, isLoading: feedLoading } = useActivity(undefined)
  const { data: delegations, isLoading: delegationsLoading } = useDelegations(undefined)
  const { data: modules, isLoading: modulesLoading } = useModules()

  return (
    <div className="mx-auto max-w-3xl space-y-8">
      <header>
        <h1 className="text-xl font-semibold">Observability</h1>
        <p className="text-sm text-muted-foreground">
          Who is using auth, what is loaded, and what has happened — across everyone. Secret-free.
        </p>
      </header>

      <Section title="Activity" description="Every brokering decision in the recent window, newest first.">
        <ActivityFeed feed={feed} isLoading={feedLoading} emptyHint="No activity recorded yet." />
      </Section>

      <Section
        title="Who is using auth"
        description="Every grant: which caller may act, as whom, on what — including pure automation."
      >
        <DelegationsCard
          delegations={delegations}
          isLoading={delegationsLoading}
          emptyHint="No delegations are configured."
        />
      </Section>

      <Section title="Loaded modules" description="Every connector the broker has, its egress posture, and usage.">
        <ModulesCard modules={modules} isLoading={modulesLoading} />
      </Section>
    </div>
  )
}
