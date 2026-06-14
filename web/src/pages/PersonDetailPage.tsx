import { ArrowLeft } from 'lucide-react'
import { Link, useParams } from 'react-router-dom'
import { Alert, AlertDescription } from '../components/ui/alert'
import { Button } from '../components/ui/button'
import { PlaceholderCard } from '../components/common/PlaceholderCard'
import { RoleBadge } from '../components/badges/RoleBadge'
import { people } from '../data/fixtures'

export function PersonDetailPage() {
  const { principal } = useParams()
  const decoded = principal ? decodeURIComponent(principal) : ''
  const person = people.find((candidate) => candidate.principal === decoded)

  return (
    <div className="flex flex-col gap-4">
      <Button asChild variant="ghost" size="sm" className="self-start">
        <Link to="/admin/users">
          <ArrowLeft className="h-4 w-4" aria-hidden />
          Users
        </Link>
      </Button>

      <div className="flex flex-wrap items-center gap-3">
        <h1 className="text-xl font-semibold tracking-tight">{decoded || 'Person'}</h1>
        {person ? <RoleBadge role={person.role} /> : null}
      </div>

      <Alert variant="warning">
        <AlertDescription className="text-foreground">
          Viewing another person's accounts is step-up gated and audited.
        </AlertDescription>
      </Alert>

      <PlaceholderCard
        title="Scoped accounts"
        body="This person's accounts — the same My-accounts table, scoped to one owner — land in a later phase."
      />
    </div>
  )
}
