# Jobs Dogfood Report

## Result

Internal durable scheduler: PASS
Live external Job: BLOCKED_EXTERNAL

## Verified

- run-now, once, daily, and weekday schedule contracts;
- timezone plus UTC next occurrence;
- unique logical occurrence and lease fencing;
- controlled recurrence;
- pause/resume/cancel;
- restart recovery without duplicate occurrence;
- interrupted RUNNING read traces reset only under the recovered Chat or fenced Job owner;
- completed read traces replay without provider duplication;
- Memory/context snapshot, model call, local/GitHub tools, outputs, Evidence, and history;
- explicit Account/capability/side-effect grants through Job Access;
- side effects pause in `WAITING_FOR_APPROVAL` and continue deterministically;
- Account/plugin lifecycle recomputes dependent Job health READY/BLOCKED;
- Jobs and run details poll while visible.

## Topology

One active scheduler/Broker is supported. Leases/fences protect restart and stale workers, not an unclaimed multi-node deployment.

## Dogfood Definitions

Local continuity Job: weekday summary using model plus Memory/FollowUps. Requires a configured model but no external Account.

External read Job: open issue summary using explicit GitHub Account and `github.issues.list`. Remains visibly blocked until live GitHub configuration exists.