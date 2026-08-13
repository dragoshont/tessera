# R2 Job Model

Job is an owner-scoped durable instruction with desired state (`DRAFT`, `ACTIVE`, `PAUSED`, `CANCELED`), operational health (`READY`, `DEGRADED`, `BLOCKED`), instruction, model profile, schedule, next occurrence, account/capability/context/side-effect grants, timestamps, and optimistic version. Running is represented by JobRun, not desired state.

JobRun is unique for `(ownerPrincipalId, jobId, scheduledFor)` and uses the legal transitions and Action-state crosswalk in `R2_DATA_MODEL.md`. It records context reference, model, capability/account/action refs, checkpoints, output/evidence refs, bounded error, lease fencing generation, and timestamps. Its detail projection dereferences those records into bounded product-safe capability/account use, durable Action approval/verification, output, Evidence, and sequenced trace entries; it never exposes hidden prompts, chain-of-thought, secrets, or raw provider bodies. Chat-created Jobs are structured proposals and remain DRAFT until reviewed. The same ExecutionCoordinator and approval rules used by Chat execute every run.

`DEVELOPMENT` is a typed Job specialization governed by
`DEVELOPMENT_WORKSPACE.md`. It binds a canonical Conversation and immutable
server-owned snapshot, does not require a model profile, and dispatches only a
server-resolved command profile through the isolated executor. It reuses the same
JobRun lease, terminal states, bounded outputs, and exact Action approval semantics;
the proof slice exposes only a read-only profile and rejects writes.
