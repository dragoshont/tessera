# Tessera R2 Product Spec

**Status:** Implementation route summary. The normative requirements remain `docs/tessera/r1/r2-spec.md`; `R2_API_CONTRACT.md` and `R2_DATA_MODEL.md` provide the exact implementation contract.

R2 is one authenticated, owner-scoped product in the existing Broker host. A user can configure a real model account, connect a real GitHub account, use durable Chat and memory, approve exact external actions, create durable Jobs, and inspect activity after restart. Live remote verification is blocked until the owner supplies configuration; the production implementations are never replaced by fixtures.

## Product Navigation

The default route is Chat. Primary navigation is Chat, Jobs, Accounts, Plugins, Memory, Activity, and Settings. Existing operator surfaces move under Settings/Admin.

## API Contract

All routes are under `/api/v1`, require the existing authenticated Broker principal except the explicitly enabled non-production loopback development identity, and derive `ownerPrincipalId` server-side. Client-supplied owner IDs are rejected. Other-owner resources return `404`.

| Method | Route | Contract |
|---|---|---|
| GET/POST | `/conversations` | List/create owner conversations |
| GET/PATCH/DELETE | `/conversations/{id}` | Read, rename/archive, or delete using `expectedVersion` |
| GET/POST | `/conversations/{id}/messages` | List/send; user message commits before model dispatch |
| POST | `/conversations/{id}/retry` | Idempotent retry of one failed assistant turn |
| POST | `/conversations/{id}/stop` | Persist cancellation request |
| GET | `/conversations/{id}/events?after={sequence}` | Resumable SSE of public execution events |
| GET/POST | `/accounts` | List/create metadata and opaque credential reference |
| POST | `/accounts/{id}/validate` | Real adapter validation; updates health |
| POST | `/accounts/{id}/disable` | Disable future dispatch |
| DELETE | `/accounts/{id}` | Revoke metadata and request credential deletion through custody |
| GET | `/plugins` and `/plugins/{id}/versions/{version}` | List or inspect validated installed trusted-local packages |
| POST | `/plugins/{id}/versions/{version}/enable` or `/disable` | Exact-version lifecycle mutation |
| GET/PUT | `/plugins/{id}/versions/{version}/configuration` | Read or replace manifest-declared non-secret configuration |
| DELETE | `/plugins/{id}/versions/{version}` | Safely remove configuration/package availability while retaining historical descriptors |
| GET | `/capabilities` | Principal/account/grant/policy-filtered availability |
| GET/POST | `/actions` | Filter/list pending or historical Actions and create exact proposals through the coordinator |
| GET | `/actions/{id}` | Read exact approval, execution, and verification state |
| POST | `/actions/{id}/approve` | Consume exact one-use approval |
| POST | `/actions/{id}/cancel` | Cancel; edits create a new Action |
| GET/POST | `/jobs` | List/create reviewed Jobs |
| GET/PATCH/DELETE | `/jobs/{id}` | Read/update/cancel with optimistic version |
| POST | `/jobs/{id}/run` | Idempotent run-now request |
| POST | `/jobs/{id}/pause` or `/resume` | Desired-state transition |
| GET | `/jobs/{id}/runs` | Durable run history |
| GET | `/job-runs/{id}` and child collections | Bounded paged product execution trace with capability/account use, Actions/approval/verification, outputs, errors, and Evidence; never chain-of-thought |
| GET/POST | `/memory` | Filter/search current/history or explicitly remember |
| POST | `/memory/{id}/correct` | Supersede through user Evidence/Assertion |
| GET | `/memory/{id}/history` | Exact correction and stop-using change history |
| GET | `/memory/{id}/why` | Exact provenance projection |
| POST | `/memory/{id}/stop-using` | Exclude from context without false erasure claim |
| GET | `/memory/follow-ups` | Filter/search current FollowUps with field and Evidence projections |
| GET | `/activity` | Filter/search bounded Evidence/Event/Action/Job activity projection |
| GET/PATCH | `/settings` | Model defaults, timezone, approval and memory controls |
| GET/POST | `/settings/model-profiles` | List or create owner model profiles after account validation |

Lists use stable cursor pagination and a server maximum of 100. Mutations accept `Idempotency-Key` where creation/execution can be retried and `expectedVersion` where an existing aggregate changes. Idempotent responses retain their documented DTO body and expose replay through `Idempotency-Replayed`.

## Errors And Public Events

Errors use RFC 9457 Problem Details with stable `code`: `invalid_request`/`invalid_cursor` (400), `unauthenticated` (401), `forbidden` (403), `not_found` (404), `version_conflict`/`idempotency_conflict`/`invalid_state` (409), `configuration_required`/`account_ambiguous`/`invalid_model` (422), `rate_limited` (429), `provider_auth_required` (502), `provider_unavailable`/`provider_malformed` (502), `provider_timeout` (504), and `storage_unavailable` (503). Provider detail is bounded and secret-redacted.

SSE emits monotonically sequenced `status`, `text`, `capability_requested`, `approval_required`, `capability_result`, `failure`, and `completed`. Reconnect with `after`; hidden prompts and chain-of-thought are never events.

## Product Truth

Unconfigured adapters show `Configuration required`; revoked accounts and disabled plugins show recovery actions; blocked Jobs remain visible. Production routes contain no demo switch or fixture fallback.
