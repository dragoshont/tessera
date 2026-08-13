# R2 Development Workspace Contract

**Status:** Implemented proof slice; runtime deployment requires human infrastructure approval

## Product invariant

One canonical owner-scoped Conversation may create and observe development Jobs
from web, packaged macOS, and iOS. Execution and repository state are server-owned;
clients are viewers/controllers and never supply local paths or execution
infrastructure.

## Domain model

`Job.kind` is `AUTOMATION` or `DEVELOPMENT` and defaults to `AUTOMATION` for all
existing records. A `DEVELOPMENT` Job requires `conversationId` and one immutable
`DevelopmentJobSpec`:

```text
DevelopmentWorkspace {
  id, conversationId, snapshotRef, snapshotHash, state: READY|REVOKED,
  createdAt, version
}

DevelopmentJobSpec {
  jobId, workspaceId, commandProfile, arguments: string[],
  effect: READ_ONLY|WORKSPACE_WRITE, timeoutSeconds, outputLimitBytes,
  executorImageDigest
}
```

`snapshotRef`, the resolved PVC claim/subpath, namespace, image, executable,
environment, and resource limits are server configuration and are never public
mutation fields. Snapshot IDs are opaque and owner/conversation scoped. Snapshot
content is immutable for the lifetime of a run.

The first command profile is:

```text
repository.status
  effect: READ_ONLY
  executable: /usr/bin/git
  argv prefix: ["status", "--short", "--branch"]
  client arguments: none
  server environment: { "GIT_OPTIONAL_LOCKS": "0" }
```

The profile registry is server code/configuration, not plugin input. Unknown
profiles or arguments fail closed with `development_command_not_allowed`.
The wire boundary permits at most 8 arguments of at most 256 UTF-8 bytes each;
each profile may be stricter. `repository.status` requires an empty array.

## API

```text
GET /conversations/{conversationId}/development-workspaces

200 Page<DevelopmentWorkspaceDto>

POST /conversations/{conversationId}/development-tasks
Idempotency-Key: required
{
  name: string,
  workspaceId: string,
  commandProfile: "repository.status",
  arguments: []
}

202 {
  job: JobDto,
  run: JobRunDto
}
```

The list route returns only `READY` server-provisioned workspace IDs, display
names, and snapshot hashes for the authenticated owner and Conversation. Startup
configuration may register reviewed snapshot metadata, but clients cannot create
or alter it in this slice.

The operation verifies owner scope for Conversation and workspace, requires a
`READY` snapshot, resolves the command profile server-side, atomically creates an
`ACTIVE` one-shot `DEVELOPMENT` Job and queued JobRun, and returns an exact replay
for the same idempotency key. It does not require a model profile. Stable failures:

- `404 not_found`: Conversation or workspace is absent for this owner, without
  cross-owner disclosure;
- `409 idempotency_conflict`: key reused with different canonical input;
- `409 workspace_unavailable`: snapshot was revoked after selection;
- `422 development_command_not_allowed`: profile or argument shape is not allowed;
- `422 development_executor_unavailable`: executor configuration is incomplete.

Existing operations remain authoritative:

- `GET /jobs`, `GET /jobs/{id}`, and `GET /jobs/{id}/runs` expose the durable task;
- `GET /job-runs/{id}` exposes bounded output and trace;
- `GET /actions` and existing approve/cancel routes govern write profiles;
- `GET /conversations/{id}/messages` exposes a `SYSTEM_EVENT` completion/failure
  entry that references the run output without copying unbounded logs.

Additive DTO fields:

```text
JobDto + { kind, conversationId|null, developmentSpec|null }
DevelopmentSpecDto { workspaceId, commandProfile, arguments, effect,
                     timeoutSeconds, outputLimitBytes: 32768 }
JobRunOutputDto.kind += DEVELOPMENT_LOG
```

## Execution lifecycle

1. The scheduler acquires the existing fenced JobRun lease.
2. It resolves the immutable workspace and command profile again at dispatch.
3. `READ_ONLY` starts the Kubernetes executor. `WORKSPACE_WRITE` creates an exact
   Action and enters existing `WAITING_FOR_APPROVAL` state before execution.
4. The executor creates a uniquely named Job labelled with the opaque run ID. The
   pod copies the read-only PVC snapshot to `emptyDir` and runs direct argv.
5. The executor waits up to the profile timeout, collects the bounded combined
  Kubernetes container log,
   deletes/reaps the Job according to server retention policy, and returns a
   normalized result. Unknown create/watch outcomes fail as
   `development_executor_outcome_unknown`; Tessera does not blindly duplicate.
6. Before persistence, the log is UTF-8 normalized, control characters are removed,
   configured secret-like patterns are redacted, and the combined stored text is
   capped at `outputLimitBytes` (initial maximum 32 KiB). Truncation is explicit.
7. Output/checkpoint plus terminal JobRun state commit under the active fence. A
   bounded Conversation `SYSTEM_EVENT` references the Job/JobRun and terminal
   state so the canonical conversation remains the cross-client anchor.

The proof slice registers no `WORKSPACE_WRITE` profile. Any attempted write
profile fails `development_command_not_allowed`; this is capability honesty, not
an approval bypass. A later write slice must implement the Action transition in
step 3 before registering its first profile.

## Kubernetes safety contract

The adapter may set only names/labels derived from server-issued IDs and values
from `DevelopmentExecutorOptions`. The resulting pod must enforce:

- `runAsNonRoot`, fixed UID/GID, seccomp `RuntimeDefault`;
- `allowPrivilegeEscalation: false`, read-only root filesystem, all capabilities
  dropped, no privileged mode;
- `automountServiceAccountToken: false`;
- one read-only PVC snapshot subpath and one `emptyDir`; no `hostPath`, socket, or
  arbitrary volume type;
- CPU/memory/ephemeral-storage limits, active deadline, and backoff limit zero;
- default-deny ingress and egress; no egress is required by the first profile;
- pinned image digest from server configuration, never a client value.

The broker ServiceAccount receives namespace-scoped least-privilege access only to
create/get Jobs and list/read their Pods/logs. Identity, RBAC, PVC, and
NetworkPolicy manifests are plan-only in this repository and require human apply.

## Persistence and rollback

Migration v17 is additive:

- `jobs.kind TEXT NOT NULL DEFAULT 'AUTOMATION'` with closed values;
- `jobs.conversation_id TEXT NULL`; the new spec table enforces the owner-inclusive
  Conversation relationship without rebuilding the existing Jobs table;
- `development_workspaces` with owner-inclusive Conversation identity and opaque
  server snapshot reference/hash;
- `development_job_specs`, one-to-one with owner/Job and owner/Conversation foreign
  keys, with closed effect and bounded JSON arguments.

No existing row is rewritten beyond SQLite's additive defaults. Rollback is:
pause development dispatch, allow or cancel active development runs, deploy the
older binary, and retain v17 tables/columns. Re-upgrade resumes queued work through
existing lease recovery. No down migration or destructive cleanup runs.

## Future contract, not this implementation

- Agents are durable orchestrators that create typed Jobs and consume bounded
  evidence; they do not own identity, approvals, or execution infrastructure.
- MCP servers expose reviewed domain tools and may request typed development tasks;
  they cannot supply executable code, paths, images, or unrestricted network.
- Apps are client surfaces over canonical Conversations/Jobs/Actions.
- Plugins remain reviewed, hash-pinned, disabled by default, and may eventually
  declare command-profile metadata only after a separate trust/compatibility ADR.
- Repository acquisition, writable patch artifacts, build caches, package egress,
  interactive terminals, parallel agents, and persistent workspaces are later
  slices with separate contracts and threat models.