# ADR 0035: Server-owned isolated development workspaces

**Status:** Accepted; runtime deployment requires human infrastructure approval

## Context

Tessera already owns durable, owner-scoped Conversations, Jobs, Actions,
approvals, outputs, and cross-client API state. It does not own a repository
workspace or an execution boundary suitable for software-development commands.
Running commands in a client, in the broker process, through a host Docker socket,
or against a client-supplied path would bypass Tessera's ownership, approval,
durability, and isolation guarantees.

Official public descriptions of ChatGPT/Codex support the product pattern of a
durable task that is independent of the viewing client and executes in an isolated
cloud environment. Undocumented implementation details are not part of this
decision.

## Decision

Development work is a typed specialization of the existing Job/JobRun model.
Every development Job belongs to exactly one owner and Conversation, references a
server-owned immutable repository snapshot by opaque ID, and resolves a
server-configured command profile. Public requests never carry a filesystem path,
repository URL, container image, namespace, executable, environment variable, or
shell command.

The first executor is a Kubernetes Job adapter behind an `IDevelopmentExecutor`
port. It creates one short-lived, non-privileged Job per JobRun. An init container
copies a read-only server snapshot from a configured PVC subpath into an
`emptyDir`; the command container receives only the server-resolved executable and
argument array. It has a read-only root filesystem, no privilege escalation, all
Linux capabilities dropped, bounded CPU/memory/time/output, no service-account
token, and a label selected by a default-deny egress NetworkPolicy. No host path or
container-runtime socket is mounted.

Command profiles carry a server-owned effect classification. `READ_ONLY` profiles
may dispatch directly. `WORKSPACE_WRITE` profiles first create an exact existing
Action proposal bound to owner, Conversation, Job, JobRun, workspace snapshot,
profile, arguments, and input hash. Approval authorizes one execution; it does not
mark the run successful. The initial implementation exposes one read-only profile,
`repository.status`; write profiles remain contract-only until a concrete editing
slice defines durable patch output and review semantics.

One bounded redacted combined container log is persisted as existing JobRun output and trace
state. The canonical Conversation receives a bounded system-event message that
references the JobRun output; all clients continue through existing owner-scoped
Conversation, Job, Action, and JobRun APIs.

Repository acquisition, arbitrary command execution, interactive terminals,
internet package installation, persistent writable workspaces, privileged builds,
and runtime plugin installation are out of scope.

## Consequences

- Conversation continuity, ownership, leases, approvals, output projection, and
  client-independent viewing reuse existing Tessera abstractions.
- A server operator must provision reviewed snapshots and the isolated executor
  image/PVC before a development task can run.
- Kubernetes RBAC and NetworkPolicy changes are plan-only and require human apply.
- Future agents orchestrate typed Jobs; MCP servers, apps, and hash-pinned plugins
  may contribute reviewed command profiles or context, but none can bypass the
  executor policy or become an arbitrary code loader.
- Older binaries ignore the additive schema and cannot dispatch development Jobs;
  rollback therefore pauses development dispatch and leaves history readable after
  re-upgrade.

## Rejected alternatives

- A separate development service duplicates owner scope, Jobs, Actions, SSE, and
  client APIs.
- Broker-process execution, a Docker socket, privileged containers, host paths, and
  shell strings violate the trust boundary.
- Client-owned repositories break canonical continuity and cannot be authorized by
  the server.