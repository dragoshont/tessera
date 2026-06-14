import { useMemo, useState } from 'react'
import {
  createColumnHelper,
  flexRender,
  getCoreRowModel,
  getSortedRowModel,
  useReactTable,
  type SortDirection,
  type SortingState,
} from '@tanstack/react-table'
import {
  Activity,
  AlertTriangle,
  ArrowDown,
  ArrowUp,
  ArrowUpDown,
  Ban,
  MoreHorizontal,
  Plus,
  RotateCw,
} from 'lucide-react'
import type { Connection } from '../../data/types'
import { needsAttention, relativeTime } from '../../lib/format'
import { Alert, AlertDescription } from '../ui/alert'
import { Button } from '../ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '../ui/dropdown-menu'
import { Skeleton } from '../ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../ui/table'
import { HealthBadge } from '../badges/HealthBadge'
import { ProviderIcon } from '../common/ProviderIcon'
import { AccountsEmptyState } from './AccountsEmptyState'
import { ExpiryCell } from './ExpiryCell'

export type AccountRowAction = 'reseed' | 'seed' | 'revoke' | 'view-activity'

export interface AccountsTableProps {
  connections: Connection[]
  ownerPrincipal: string
  title?: string
  isLoading?: boolean
  hasError?: boolean
  onSelectConnection?: (connectionId: string) => void
  onRowAction?: (action: AccountRowAction, connection: Connection) => void
  onConnectAccount?: () => void
  onReseedAllExpiring?: () => void
  onRetry?: () => void
}

const columnHelper = createColumnHelper<Connection>()

function SortGlyph({ direction }: { direction: SortDirection | false }) {
  if (direction === 'asc') return <ArrowUp className="h-3 w-3" aria-hidden />
  if (direction === 'desc') return <ArrowDown className="h-3 w-3" aria-hidden />
  return <ArrowUpDown className="h-3 w-3 opacity-40" aria-hidden />
}

function RowActions({
  connection,
  onRowAction,
  onSelectConnection,
}: {
  connection: Connection
  onRowAction?: (action: AccountRowAction, connection: Connection) => void
  onSelectConnection?: (connectionId: string) => void
}) {
  const isAbsent = connection.status === 'absent'
  return (
    // Stop row-navigation when the operator is interacting with the menu.
    <div className="flex justify-end" onClick={(event) => event.stopPropagation()}>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="ghost" size="icon" aria-label={`Actions for ${connection.displayName}`}>
            <MoreHorizontal className="h-4 w-4" />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent>
          {isAbsent ? (
            <DropdownMenuItem onSelect={() => onRowAction?.('seed', connection)}>
              <RotateCw className="h-4 w-4" aria-hidden />
              Seed now
            </DropdownMenuItem>
          ) : (
            <DropdownMenuItem onSelect={() => onRowAction?.('reseed', connection)}>
              <RotateCw className="h-4 w-4" aria-hidden />
              Re-seed
            </DropdownMenuItem>
          )}
          <DropdownMenuItem
            onSelect={() => {
              onRowAction?.('view-activity', connection)
              onSelectConnection?.(connection.connectionId)
            }}
          >
            <Activity className="h-4 w-4" aria-hidden />
            View activity
          </DropdownMenuItem>
          <DropdownMenuSeparator />
          <DropdownMenuItem
            className="text-health-error focus:bg-health-error/10"
            onSelect={() => onRowAction?.('revoke', connection)}
          >
            <Ban className="h-4 w-4" aria-hidden />
            {isAbsent ? 'Remove' : 'Revoke…'}
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </div>
  )
}

function LoadingRows() {
  return (
    <div className="flex flex-col gap-px rounded-xl border border-border bg-card p-2">
      {Array.from({ length: 4 }).map((_, index) => (
        <div key={index} className="flex items-center gap-3 px-2 py-3">
          <Skeleton className="h-8 w-8 rounded-lg" />
          <Skeleton className="h-4 w-40" />
          <Skeleton className="ml-auto h-4 w-24" />
          <Skeleton className="h-4 w-20" />
        </div>
      ))}
    </div>
  )
}

export function AccountsTable({
  connections,
  ownerPrincipal,
  title = 'My accounts',
  isLoading = false,
  hasError = false,
  onSelectConnection,
  onRowAction,
  onConnectAccount,
  onReseedAllExpiring,
  onRetry,
}: AccountsTableProps) {
  const [sorting, setSorting] = useState<SortingState>([])

  const columns = useMemo(
    () => [
      columnHelper.accessor('displayName', {
        header: 'Provider',
        cell: ({ row }) => (
          <div className="flex items-center gap-3">
            <ProviderIcon provider={row.original.provider} />
            <span className="font-medium">{row.original.displayName}</span>
          </div>
        ),
      }),
      columnHelper.accessor('status', {
        header: 'Health',
        enableSorting: false,
        cell: ({ getValue }) => <HealthBadge status={getValue()} />,
      }),
      columnHelper.accessor('lastSeededAt', {
        header: 'Last re-seeded',
        cell: ({ getValue }) => {
          const value = getValue()
          return <span className="text-muted-foreground">{value ? relativeTime(value) : '—'}</span>
        },
      }),
      columnHelper.display({
        id: 'expires',
        header: 'Expires',
        cell: ({ row }) => <ExpiryCell connection={row.original} />,
      }),
      columnHelper.display({
        id: 'actions',
        header: () => <span className="sr-only">Actions</span>,
        cell: ({ row }) => (
          <RowActions
            connection={row.original}
            onRowAction={onRowAction}
            onSelectConnection={onSelectConnection}
          />
        ),
      }),
    ],
    [onRowAction, onSelectConnection],
  )

  const table = useReactTable({
    data: connections,
    columns,
    state: { sorting },
    onSortingChange: setSorting,
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: getSortedRowModel(),
  })

  const attentionCount = connections.filter(needsAttention).length
  const expiringCount = connections.filter((connection) => connection.status === 'expiring_soon').length

  return (
    <section className="flex flex-col gap-4">
      <header className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-xl font-semibold tracking-tight">{title}</h1>
        <Button onClick={onConnectAccount}>
          <Plus className="h-4 w-4" aria-hidden />
          Connect account
        </Button>
      </header>

      {hasError ? (
        <Alert variant="destructive">
          <AlertDescription className="flex flex-wrap items-center justify-between gap-3">
            <span className="text-foreground">Couldn't load your accounts.</span>
            <Button variant="outline" size="sm" onClick={onRetry}>
              Retry
            </Button>
          </AlertDescription>
        </Alert>
      ) : isLoading ? (
        <LoadingRows />
      ) : connections.length === 0 ? (
        <AccountsEmptyState onConnectAccount={onConnectAccount} />
      ) : (
        <>
          {attentionCount > 0 ? (
            <Alert variant="warning">
              <AlertDescription className="flex flex-wrap items-center justify-between gap-3">
                <span className="flex items-center gap-2 text-foreground">
                  <AlertTriangle className="h-4 w-4 text-health-expiring" aria-hidden />
                  {attentionCount} {attentionCount === 1 ? 'account needs' : 'accounts need'} attention.
                </span>
                {expiringCount > 0 ? (
                  <Button variant="outline" size="sm" onClick={onReseedAllExpiring}>
                    Re-seed all expiring ({expiringCount})
                  </Button>
                ) : null}
              </AlertDescription>
            </Alert>
          ) : null}

          <div className="rounded-xl border border-border bg-card">
            <Table>
              <TableHeader>
                {table.getHeaderGroups().map((headerGroup) => (
                  <TableRow key={headerGroup.id}>
                    {headerGroup.headers.map((header) => (
                      <TableHead key={header.id} className={header.column.id === 'actions' ? 'w-12' : undefined}>
                        {header.isPlaceholder ? null : header.column.getCanSort() ? (
                          <button
                            type="button"
                            className="inline-flex items-center gap-1 uppercase tracking-wide hover:text-foreground"
                            onClick={header.column.getToggleSortingHandler()}
                          >
                            {flexRender(header.column.columnDef.header, header.getContext())}
                            <SortGlyph direction={header.column.getIsSorted()} />
                          </button>
                        ) : (
                          flexRender(header.column.columnDef.header, header.getContext())
                        )}
                      </TableHead>
                    ))}
                  </TableRow>
                ))}
              </TableHeader>
              <TableBody>
                {table.getRowModel().rows.map((row) => (
                  <TableRow
                    key={row.id}
                    tabIndex={0}
                    className="cursor-pointer outline-none hover:bg-muted/60 focus-visible:bg-muted/60"
                    onClick={() => onSelectConnection?.(row.original.connectionId)}
                    onKeyDown={(event) => {
                      if (event.key === 'Enter' || event.key === ' ') {
                        event.preventDefault()
                        onSelectConnection?.(row.original.connectionId)
                      }
                    }}
                  >
                    {row.getVisibleCells().map((cell) => (
                      <TableCell key={cell.id}>
                        {flexRender(cell.column.columnDef.cell, cell.getContext())}
                      </TableCell>
                    ))}
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>

          <p className="px-1 text-sm text-muted-foreground">
            {connections.length} {connections.length === 1 ? 'connection' : 'connections'} · acting as{' '}
            {ownerPrincipal}
          </p>
        </>
      )}
    </section>
  )
}
