# Tessera R2 Product Architecture

**Status:** Accepted implementation architecture

## Shape

R2 extends the existing .NET modular monolith: one `Tessera.Broker` host, one explicit SQLite product store, existing credential custody, and one `ExecutionCoordinator` used by Chat and Jobs. It reuses Kernel Evidence, Events, Assertions, ContextEnvelope, Actions, Authorizations, Workflows, and FollowUps. React uses only `/api/v1` in production.

```text
Chat / Scheduler -> ExecutionCoordinator -> ContextBuilder -> ModelAdapter
                                             |                 |
                                             v                 v
                                CapabilityAvailability -> structured proposal
                                             |
                         policy -> Action/approval -> executor -> verify
                                             |
                                  Evidence/Event -> coordinator result

                Conversation -> Development Job -> DevelopmentExecutor -> ephemeral Kubernetes Job
                              |                    |
                              v                    v
                          Action approval       bounded redacted Job output
```

## Boundaries

- Core owns provider-neutral records, validation, coordinator policy, and ports.
- SQLite owns additive schema/repositories/leases, not credentials.
- Broker owns authenticated `/api/v1`, SSE, composition, and hosted scheduler lifecycle.
- Authentication boundaries resolve the caller on every request but register the principal
    row only for unsafe (state-changing) methods. `GET`, `HEAD`, `OPTIONS`, and `TRACE` are
    safe per RFC 9110 §9.2.1 and change no product state, so an authenticated read persists
    nothing; the owner foreign key is established by the mutation that needs it. Registration
    is idempotent and lives in exactly one helper, so authentication, authorization, error
    ordering, fail-closed behavior, and audit semantics are unaffected.
- Providers own constrained HTTP transport. A route is fixed by trusted manifest; model/user output cannot select a URL.
- Credential stores own secret values. Product tables contain opaque references only.
- Plugins declare schemas and known executor kinds; they never receive global database or raw custody access.
- UI renders real DTOs and honest empty/blocked/error states; fixtures live only in tests/Storybook.
- Development execution is a typed Job specialization. Core owns its strict
    command/workspace contract, Broker owns orchestration, and an executor adapter
    owns Kubernetes API mechanics. The executor never receives client-selected
    paths, images, executables, namespaces, mounts, or egress policy.

## Data And Concurrency

Ordered migrations v5-v7 expand v1-v4 with registry, conversation/execution, and Job tables as specified by `R2_DATA_MODEL.md`. Every aggregate is owner-scoped and optimistic-versioned. Creation receipts bind owner plus idempotency key and request hash. Job runs are unique by `(owner, job, scheduledFor)`; leases carry expiry and fencing generation. Rollback drains dispatch and pauses scheduling before using an old binary; no destructive down migration runs.

## Dispatch Invariants

Availability is evaluated for principal, plugin/version, account lifecycle/permissions, conversation/job grants, side-effect policy, and model support, then re-evaluated immediately before dispatch. Consequential account ambiguity fails closed. Approval binds owner, Action, exact canonical payload hash, account, target, plugin/capability versions, expiry, and one use. Approval never marks success. Unknown external outcomes enter reconciliation and are not retried blindly.

## Context And Memory

The coordinator builds bounded ContextEnvelope candidates from recent messages, accepted Assertions, selected FollowUps, relevant Evidence/Jobs/accounts/capabilities, and explicit grants. Conversation text is not durable personal truth. Remember/Correct creates user Evidence and Assertions with provenance. Stop-using excludes context; erase is claimed only when custody/backup semantics can prove it.

## Out Of Scope

The proof slice has no installable agent registry or third-party app UI sandbox;
those contracts are defined in `EXTENSION_MODEL.md` and require separate slices.
Model-owned memory, plugin-owned canonical state, graph/vector store,
microservices, arbitrary remote code, arbitrary HTTP URLs, interactive
development shells, client-owned repository paths, and autonomous consequential
communication remain out of scope. Kubernetes is optional for ordinary Tessera
operation and required only when the isolated development executor is enabled.
