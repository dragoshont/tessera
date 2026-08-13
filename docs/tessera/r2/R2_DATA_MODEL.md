# R2 Data And Migration Contract

## Migration Sequence

Phase 1 adds three ordered, additive migrations and repositories before behavior phases begin:

- **v5 Product registry:** `connected_accounts`, `account_permissions`, `account_capability_bindings`, `credential_cleanup_receipts`, `plugin_installations`, `model_profiles`, `idempotency_receipts`.
- **v6 Conversation execution:** `conversations`, `messages`, `message_parts`, `capability_calls`, `capability_results`, `context_snapshot_refs`, `execution_events`; R0 `actions`/`action_authorizations` gain additive exact-binding columns for account, target, plugin version, expiry and execution reference.
- **v7 Jobs:** `jobs`, `job_account_grants`, `job_capability_grants`, `job_side_effect_grants`, `job_runs`, `job_run_checkpoints`, `scheduler_leases`, `job_outputs`.
- **v8 Custody recovery:** `orphan_credential_cleanup_receipts` records secret-free failed-compensation cleanup intent.
- **v9 Exact execution recovery:** `durable_execution_requests` atomically binds an Action to its exact non-secret execution request; `execution_controls` records generation stop state; plugin installations gain an additive removal marker while retaining their historical descriptor.
- **v17 Isolated development:** Jobs gain additive kind/conversation identity; `development_workspaces` records owner/conversation-scoped immutable server snapshots; `development_job_specs` binds a Job to one server-resolved command profile and closed effect class.

All tables use owner plus ID primary keys and foreign keys to `principals`. Aggregate tables have `version >= 1`; timestamps are UTC text. Child foreign keys include owner. Indexes cover owner/state/time lists. No secret, raw model reasoning, hidden prompt, provider body, or arbitrary diagnostics column exists.

## Constraints

- account provider is not unique; `(owner, accountId)` is identity;
- plugin ID/version/package hash identifies one installation; enabled version is explicit;
- model profile references one model account owned by the same principal;
- conversation event sequence is unique `(owner, conversation, sequence)`;
- message parts use a closed `kind` and exactly the fields legal for that kind;
- capability/action history records plugin and capability versions;
- job run is unique `(owner, job, scheduledFor)`;
- one scheduler lease row per run; fence monotonically increments;
- idempotency receipt is unique `(owner, routeFamily, key)` and stores request hash plus response reference;
- cleanup receipts contain opaque reference only, never bundle values.
- development requests contain opaque workspace IDs and command-profile IDs only;
	client paths, repository URLs, images, namespaces, executables, environment,
	credentials, and raw Kubernetes manifests are never persisted request fields.

SQLite `CHECK` constraints enforce closed states and nonnegative limits. Repository transactions enforce legal transitions and optimistic versions because transition legality depends on current rows.

## State Machines

| Aggregate | Legal transitions |
|---|---|
| Conversation | ACTIVE -> ARCHIVED/DELETED; ARCHIVED -> ACTIVE/DELETED |
| Message | PERSISTED -> RUNNING -> COMPLETED/FAILED/STOPPED; FAILED/STOPPED is retried as a new message linked by `retryOf` |
| Account | CONNECTING -> CONNECTED/DEGRADED/AUTH_REQUIRED/ERROR; CONNECTED -> DEGRADED/AUTH_REQUIRED/DISABLED/REVOKED; DEGRADED/AUTH_REQUIRED/ERROR -> CONNECTED/DISABLED/REVOKED; DISABLED -> CONNECTED/REVOKED; REVOKED terminal |
| Action | PROPOSED -> AUTHORIZED/CANCELED/EXPIRED; AUTHORIZED -> RUNNING/CANCELED; RUNNING -> VERIFIED/FAILED/RECONCILIATION_REQUIRED; RECONCILIATION_REQUIRED -> VERIFIED/FAILED; terminal states immutable |
| Job desired | DRAFT -> ACTIVE/CANCELED; ACTIVE -> PAUSED/CANCELED; PAUSED -> ACTIVE/CANCELED; CANCELED terminal |
| JobRun | QUEUED -> RUNNING/CANCELED; RUNNING -> WAITING_FOR_APPROVAL/RECONCILIATION_REQUIRED/SUCCEEDED/FAILED/CANCELED; WAITING_FOR_APPROVAL -> RUNNING/CANCELED; RECONCILIATION_REQUIRED -> RUNNING/SUCCEEDED/FAILED/CANCELED; terminal states immutable |

Action state is the external-effect truth. JobRun `WAITING_FOR_APPROVAL` means its referenced Action is `PROPOSED`; JobRun returns to `RUNNING` only after the Action is `AUTHORIZED`; JobRun is `RECONCILIATION_REQUIRED` while any required Action is so; JobRun can succeed only when every required Action is `VERIFIED`. Approval UI labels map directly to Action state, not a separate state machine.

## Transactions And Recovery

Aggregate update plus event/checkpoint/idempotency receipt commits atomically. Authorization consumption plus Action reservation remains one transaction. Scheduling inserts the unique run and advances next occurrence atomically. Lease acquisition increments fence; every worker write compares it. Expired work resumes from the last checkpoint, except an unknown provider outcome enters reconciliation.

Before rolling an R2 binary back to an R1 binary, stop new dispatch, pause the scheduler, let safe in-flight reads finish, and leave unresolved effects in durable Action reconciliation state. Earlier binaries ignore the additive v9 tables and plugin column; no down migration runs. Re-upgrading resumes from durable requests/checkpoints; v1-v8 rows are never rewritten or dropped.

Before rolling a v17 binary back, stop development dispatch and allow or cancel
active development runs. Retain the additive columns and tables; no down migration
runs. Existing automation Jobs remain `AUTOMATION` through the column default.
