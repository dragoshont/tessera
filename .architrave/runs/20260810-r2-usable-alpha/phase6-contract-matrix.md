# Phase 6 Contract And UI Matrix

## Contract Reconciliation Boundary

The authoritative wire source is `docs/tessera/r2/R2_API_CONTRACT.md`. The web client will accept only the documented shape; server drift is repaired rather than hidden by dual-shape parsing. All list reads below return `Page<T>`, expose `nextCursor`, reject malformed or other-owner cursors, and keep owner filtering server-side. Mutations marked idempotent require a client-generated `Idempotency-Key`; the server validates visible ASCII length, reuses the existing `idempotency_receipts` table for exact replay/request-hash conflicts, and never synthesizes a key. Replays retain the exact entity DTO body and set `Idempotency-Replayed: true`; receipt-returning operations also set their existing `replayed` member.

| Surface | Exact UI-used operations | Reconciliation required before binding |
|---|---|---|
| Chat | `GET/POST /conversations`; `GET/PATCH/DELETE /conversations/{id}`; messages; retry; stop; events | Page-wrap conversations, add exact `ConversationDetail`, strict creation/send/retry/stop keys, preserve persisted user/failure turns, protected cursor and resumable event sequence |
| Accounts | list/create/validate/disable/revoke | Page-wrap list; contract create body `{pluginId,displayName,nonSecretConfig,secretInput}`; resolve installed account-capable plugin server-side; version body on validate/revoke; strict keys on create/validate; secret never returned |
| Plugins | list, exact-version detail/config/enable/disable/remove; capabilities list | Preserve exact `(id,version)` address, Page wrappers, typed non-secret configuration, optimistic version, `plugin_in_use`, and blocked availability |
| Jobs | list/create/detail/update/cancel/run/pause/resume; runs; run detail and six child lists | Add exact `GET /jobs/{id} -> JobDto`; strict keys on create/run; real paged child projections instead of unconditional empty placeholders; public trace only |
| Actions | list/detail/propose/approve/cancel | Strict proposal/approval keys and versions; payload/account/target/version/expiry fields; only verified evidence renders success |
| Memory | list/create/correct/history/why/stop-using/follow-ups | Strict keys on create/correct, correction `expectedVersion`, Page wrappers, exact evidence/lineage, no erasure claim |
| Activity | product activity plus Action queue | Page wrappers and literal filters; bounded product-safe summaries only |
| Settings | settings get/patch; model-profile list/create | `ModelProfileDto`, `GET Page<ModelProfileDto>`, and idempotent `POST /settings/model-profiles` are defined in the authoritative contract; profile creation follows real account validation and never returns secret input |
| Common | all operations above | RFC 9457 fields `type,title,status,detail,instance,code,traceId`; typed client `R2Problem`; 1-100 limits; protected owner-bound cursors; optimistic conflicts stay visible |

## Component Inventory And Story States

| Component | Required isolated states before composition |
|---|---|
| `ChatWorkspace` | empty, loading, configuration required, persisted outage, capability call/result with evidence, approval required, recovered/resumed, failed/retryable, stopped |
| `ActionApprovalCard` | proposed, approving, running, verified, failed, unknown/reconciling, expired, canceled; exact payload/account/target/plugin/capability/expiry fields and Edit-as-cancel/new-proposal semantics |
| `JobsTable` | loading, empty, populated, blocked, paused, mobile rows |
| `JobEditor` | run-now, one-time, daily, weekday, grant selection, review-before-create, validation error |
| `JobDetail` / `JobRunTimeline` | ready, blocked dependency, queued, running, waiting approval, resumed, verified output/evidence, failed, unknown/reconciling |
| `AccountList` / `AccountWizard` / account detail | loading, empty, multiple accounts, write-only secret, permissions review, validating, healthy, auth required, disabled, revoked cleanup/recovery, mobile rows |
| `PluginList` / plugin detail/config | loading, empty catalog, exact versions, enabled, disabled with affected capability/Job recovery, invalid config, `plugin_in_use`, mobile rows |
| `MemoryExplorer` / memory detail | current, search/no-results, history, Why provenance, Correct review, stopped/excluded, FollowUps |
| `ActivityList` | loading, empty, filtered product events, approval queue, verified/failed/unknown outcomes, mobile rows |
| `SettingsPanel` | configuration required, configured profiles/accounts, saving, validation/provider error, timezone/approval/memory summaries, legacy Admin links |

All table components use semantic tables on desktop and labeled rows on narrow viewports. Dialogs/sheets trap focus, close on Escape, return focus, use non-color state labels, visible focus, touch-safe targets, and reduced-motion-safe transitions.

The implementation extends the existing `ChatWorkspace`; composes `HealthBadge`, table, dialog, sheet, tabs, input, alert, and button primitives; and reuses the table/drawer anatomy of `AccountsTable`/`ConnectionDrawer` plus the timeline anatomy of `ActivityFeed`. `AccountList` and `ActivityList` are R2 contract adapters around those existing visual abstractions, not parallel legacy product routes. New domain components exist only where the R2 contract has no current Storybook abstraction: Action approval, Jobs, Plugins, Memory, and Settings.

## Adversarial And Journey Evidence

- Journey B: Remember from Chat -> Memory search -> Why evidence/lineage -> Correct with version -> history; Stop using states that data remains in history.
- Journey D: Chat capability requested/result parts render inline with bounded result and evidence; provider/configuration failure remains persisted and retryable.
- Journey E: exact proposal -> Approve -> running -> verified result; UI also renders expired, replay/conflict, disabled-plugin, revoked-account, failed, and unknown/reconciliation outcomes. Backend endpoint tests enforce other-owner and substitution failures.
- Journey F/H: deterministic explicit Chat-to-Job proposal -> review -> POST -> history -> waiting approval -> approval continuation/reconciliation; no fake model response.
- Journey I: exact-version disable -> capability unavailable and Job blocked recovery -> re-enable; no marketplace claim.
- Journey J: affected-state warning -> type the account display name to confirm -> 202 revoked state -> affected capability/Job recovery -> reconnect by adding/validating a new account; Escape/focus return are tested and no restore or secret-recovery claim is made.

Playwright intercepts transport to prove UI requests and states only. Backend endpoint tests prove pagination, headers, replay/conflict, versions, owner isolation, expiry/replay/substitution, revoke/disable dispatch checks, and child projections. Component/client tests also cover hostile provider/capability text rendered only as inert text, rapid duplicate clicks, stale versions, secret non-echo, and cancellation/error recovery. Live model/GitHub calls remain `BLOCKED_EXTERNAL` without credentials.