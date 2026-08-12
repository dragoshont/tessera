import { useState, startTransition } from 'react'
import { HttpError } from '../api/client'
import {
  useAcceptFollowUp,
  useCorrectFollowUp,
  useFollowUp,
  useFollowUpWhy,
  useFollowUps,
  useImportFollowUpFixture,
  useResolveFollowUp,
} from '../api/hooks'
import {
  FollowUpWorkspace,
  type ContinuityView,
} from '../components/continuity/FollowUpWorkspace'
import type { FollowUpField } from '../data/types'

function operationId(prefix: string): string {
  return `${prefix}-${globalThis.crypto.randomUUID()}`
}

function mutationMessage(error: unknown): string {
  if (error instanceof HttpError) {
    if (error.code === 'stale_version') return 'This follow-up changed. Close and reopen the detail before deciding.'
    if (error.code === 'invalid_state') return 'That decision no longer applies to the current follow-up state.'
    if (error.status === 409) return 'This decision conflicts with a newer continuity operation.'
    if (error.status === 503) return 'Local continuity storage is not configured.'
    if (error.status === 401) return 'Your session ended. Sign in again before deciding.'
  }
  return 'The continuity decision did not complete. Retry.'
}

function readMessage(error: unknown, surface: 'list' | 'detail'): string {
  if (error instanceof HttpError) {
    if (error.status === 401) return 'Your session ended. Sign in again to view continuity.'
    if (error.status === 503) return 'Local continuity storage is not configured.'
  }
  return surface === 'list'
    ? 'Continuity could not be loaded. Retry.'
    : 'Follow-up detail could not be loaded. Close and retry.'
}

export function ContinuityPage() {
  const [view, setView] = useState<ContinuityView>('attention')
  const [selectedId, setSelectedId] = useState<string>()
  const [mutationError, setMutationError] = useState<string | null>(null)
  const list = useFollowUps(view)
  const detail = useFollowUp(selectedId)
  const why = useFollowUpWhy(selectedId)
  const importFixture = useImportFollowUpFixture()
  const accept = useAcceptFollowUp(selectedId)
  const correct = useCorrectFollowUp(selectedId)
  const resolve = useResolveFollowUp(selectedId)
  const busy = importFixture.isPending || accept.isPending || correct.isPending || resolve.isPending

  async function run(action: () => Promise<unknown>) {
    setMutationError(null)
    try {
      await action()
    } catch (error) {
      setMutationError(mutationMessage(error))
    }
  }

  return (
    <FollowUpWorkspace
      view={view}
      items={list.data?.items}
      selected={detail.data}
      why={why.data}
      whyLoading={why.isLoading}
      whyError={why.isError}
      previewVolatile={import.meta.env.MODE === 'e2e' && navigator.webdriver}
      isLoading={list.isLoading}
      detailLoading={Boolean(selectedId) && detail.isLoading}
      listTruncated={list.data?.truncated}
      errorMessage={list.isError
        ? readMessage(list.error, 'list')
        : detail.isError
          ? readMessage(detail.error, 'detail')
          : mutationError}
      busy={busy}
      onViewChange={(nextView) => {
        startTransition(() => {
          setView(nextView)
          setSelectedId(undefined)
          setMutationError(null)
        })
      }}
      onSelect={(id) => setSelectedId(id)}
      onCloseDetail={() => setSelectedId(undefined)}
      onImportInitial={() => void run(async () => {
        const result = await importFixture.mutateAsync({
          fixtureId: 'initial',
          input: { operationId: operationId('import') },
        })
        setSelectedId((result as { followUpId: string }).followUpId)
      })}
      onImportUpdate={(fixtureId) => {
        if (!detail.data) return
        void run(() => importFixture.mutateAsync({
          fixtureId,
          input: {
            operationId: operationId(`import-${fixtureId}`),
            followUpId: detail.data!.followUpId,
            expectedVersion: detail.data!.version,
          },
        }))
      }}
      onAccept={() => {
        if (!detail.data) return
        void run(() => accept.mutateAsync({
          operationId: operationId('accept'),
          expectedVersion: detail.data!.version,
        }))
      }}
      onCorrect={(field: FollowUpField, value: string) => {
        if (!detail.data) return
        void run(() => correct.mutateAsync({
          operationId: operationId('correct'),
          expectedVersion: detail.data!.version,
          field,
          value,
        }))
      }}
      onResolve={(field: FollowUpField, value: string) => {
        if (!detail.data) return
        void run(() => resolve.mutateAsync({
          operationId: operationId('resolve'),
          expectedVersion: detail.data!.version,
          field,
          value,
        }))
      }}
    />
  )
}