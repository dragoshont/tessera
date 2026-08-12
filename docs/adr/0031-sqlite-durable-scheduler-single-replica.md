# ADR 0031 — SQLite durable scheduler and single active replica

- **Status:** Accepted (2026-08-10)

R2 Jobs use the existing SQLite product database, durable next occurrence, unique `(owner, job, scheduledFor)` runs, leases with expiry and fencing generation, and workflow checkpoints. One Broker replica is supported for Alpha; fencing still protects restart and overlapping loops. Recurring Jobs remain ACTIVE after a run. Kubernetes scaling is not introduced. Rollback leaves additive R2 tables unused and preserves v1-v4 data.
