export function ActionRequiredPage() {
  return (
    <div className="flex flex-col gap-4">
      <h1 className="text-xl font-semibold tracking-tight">Action required</h1>
      <div className="rounded-xl border border-dashed border-border bg-card px-6 py-16 text-center text-muted-foreground">
        Nothing needs you right now.
      </div>
    </div>
  )
}
