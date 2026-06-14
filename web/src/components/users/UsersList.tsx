import { useMemo } from 'react'
import {
  createColumnHelper,
  flexRender,
  getCoreRowModel,
  useReactTable,
} from '@tanstack/react-table'
import { ChevronRight } from 'lucide-react'
import type { Person } from '../../data/types'
import { cn } from '../../lib/utils'
import { Avatar } from '../ui/avatar'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../ui/table'
import { RoleBadge } from '../badges/RoleBadge'

export interface UsersListProps {
  people: Person[]
  currentPrincipal?: string
  onSelectPerson?: (principal: string) => void
}

const columnHelper = createColumnHelper<Person>()

export function UsersList({ people, currentPrincipal, onSelectPerson }: UsersListProps) {
  const columns = useMemo(
    () => [
      columnHelper.accessor('principal', {
        header: 'Person',
        cell: ({ row }) => (
          <div className="flex items-center gap-3">
            <Avatar name={row.original.principal} />
            <span className="font-medium">{row.original.principal}</span>
            {row.original.principal === currentPrincipal ? (
              <span className="text-xs text-muted-foreground">(you)</span>
            ) : null}
          </div>
        ),
      }),
      columnHelper.accessor('role', {
        header: 'Role',
        cell: ({ getValue }) => <RoleBadge role={getValue()} />,
      }),
      columnHelper.accessor('connectionCount', {
        header: 'Connections',
        cell: ({ getValue }) => <span className="tabular-nums">{getValue()}</span>,
      }),
      columnHelper.accessor('needsAttentionCount', {
        header: 'Needs attention',
        cell: ({ getValue }) => {
          const count = getValue()
          return (
            <span
              className={cn(
                'tabular-nums',
                count > 0 ? 'font-medium text-health-expiring' : 'text-muted-foreground',
              )}
            >
              {count}
            </span>
          )
        },
      }),
      columnHelper.display({
        id: 'chevron',
        header: () => <span className="sr-only">Open</span>,
        cell: () => <ChevronRight className="h-4 w-4 text-muted-foreground" aria-hidden />,
      }),
    ],
    [currentPrincipal],
  )

  const table = useReactTable({
    data: people,
    columns,
    getCoreRowModel: getCoreRowModel(),
  })

  return (
    <section className="flex flex-col gap-4">
      <header className="flex flex-col gap-1">
        <h1 className="text-xl font-semibold tracking-tight">Users</h1>
        <p className="text-sm text-muted-foreground">
          People are derived from your sign-in directory and policy. Admins are a small allow-list,
          not a database.
        </p>
      </header>

      <div className="rounded-xl border border-border bg-card">
        <Table>
          <TableHeader>
            {table.getHeaderGroups().map((headerGroup) => (
              <TableRow key={headerGroup.id}>
                {headerGroup.headers.map((header) => (
                  <TableHead key={header.id} className={header.column.id === 'chevron' ? 'w-12' : undefined}>
                    {header.isPlaceholder
                      ? null
                      : flexRender(header.column.columnDef.header, header.getContext())}
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
                onClick={() => onSelectPerson?.(row.original.principal)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter' || event.key === ' ') {
                    event.preventDefault()
                    onSelectPerson?.(row.original.principal)
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
    </section>
  )
}
