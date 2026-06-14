import { Link } from 'react-router-dom'
import { Button } from '../components/ui/button'
import { PlaceholderCard } from '../components/common/PlaceholderCard'

export function ConnectWizardPage() {
  return (
    <div className="flex flex-col gap-4">
      <h1 className="text-xl font-semibold tracking-tight">Connect an account</h1>
      <PlaceholderCard
        title="Connect wizard"
        body="The 5-step connect wizard and the live captcha hand-off land in later phases. For now, head back to your accounts."
        action={
          <Button asChild variant="outline">
            <Link to="/accounts">Back to My accounts</Link>
          </Button>
        }
      />
    </div>
  )
}
