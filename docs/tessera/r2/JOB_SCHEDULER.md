# R2 Job Scheduler

Schedules support run-now, one UTC instant, daily local time, selected weekdays, and a validated five-field cron-equivalent subset with an IANA timezone. The scheduler computes and persists the next UTC occurrence; DST gaps advance to the next valid instant and overlaps choose the earlier UTC occurrence. For `America/New_York` at local `01:30` on fall-back day, the `-04:00` occurrence runs and the repeated `-05:00` occurrence is skipped for that schedule date.

A scan transaction inserts the unique run and advances `nextOccurrence`; conflict means another loop already claimed it. A worker acquires a time-bounded lease and increments fencing generation. Every checkpoint/update requires the current fence. Startup recovers expired RUNNING leases, resuming from a durable checkpoint or entering reconciliation when an external outcome is unknown. Recurring Jobs remain ACTIVE. Pause/cancel prevents new claims; cancellation is observed between coordinator steps. No in-memory timer is authoritative.

After acquiring the same lease, the dispatcher branches on Job kind. `AUTOMATION`
uses the existing model/coordinator path. `DEVELOPMENT` re-resolves the workspace
and command profile, then calls the isolated executor without requiring a model.
Executor creation/watch ambiguity enters reconciliation and is never blindly
re-created. Output/checkpoint and terminal state writes require the current fence;
pause/cancel and expired-lease recovery retain their existing authority.
