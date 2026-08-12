# Recommended Plan

## Summary

Deliver R2 in seven autonomous, gated phases. Contracts and additive persistence lead; external adapters and capability availability follow; one coordinator then serves durable Chat and Jobs; real `/api/v1` product routes and Storybook-grounded UI bind last.

## Implementation Sequence

1. Create R2 contracts, ADRs, decision log, ordered additive migrations v5-v7, owner-scoped repositories, and migration/restart tests.
2. Add validated trusted-local plugin manifests, connected-account metadata/credential refs, model profiles, OpenAI-compatible transport, fixed-route HTTP foundation, GitHub list/create/verify integration, and availability filtering.
3. Add one ExecutionCoordinator, exact durable approvals, dispatch rechecks, receipts, verification, and unknown-outcome reconciliation.
4. Add durable conversations/messages/parts/events, ContextEnvelope assembly, explicit remember/correct/why services, resumable SSE, retry, and stop.
5. Add durable Jobs/runs/grants/schedules, IANA-timezone recurrence, SQLite lease/fencing, checkpoints, restart recovery, and coordinator reuse.
6. Add `/api/v1` endpoints and product navigation/pages; build isolated Storybook states before route composition and keep fixtures test-only.
7. Run reliability, adversarial, migration, security, dependency, repository, runtime/run, and dual-family semantic gates; write the honest final report.

Phase exits are respectively: contract and migration/repository PASS; adapter/manifest/availability PASS; exact approval/coordinator PASS; Chat/context/memory/restart PASS; scheduler/fencing/restart PASS; Storybook/API/Playwright/reconcile PASS; full deterministic and independent adversarial PASS. A phase does not advance on an unresolved Critical/High or product-completion Major.

## Test Strategy

After the first production edit run the focused SQLite migration test immediately. Thereafter run the narrow owning test project after each slice, including owner isolation, optimistic conflicts, exact approval replay/substitution, manifest traversal/hash/schema rejection, SSRF/host rejection, provider error normalization, outage persistence, scheduler fencing/restart, revocation-at-dispatch, UI empty/error/mobile/a11y behavior, and no-fixture production scans. Finish with all configured gates.

## Rollback / Recovery

Migrations v5-v7 are expand-only. Rolling back follows the drain/pause procedure in `R2_DATA_MODEL.md`, leaves R2 tables unused, and never drops or rewrites v1-v4 data. External operations use idempotency and verification; unknown outcomes enter reconciliation and are never blindly retried. Scheduler leases expire and are fenced by generation.

## Human Approval Needed

No repository implementation approval remains: autonomous continuation is explicitly authorized. No IaC apply, runtime mutation, secret access, live model call, or live GitHub mutation is authorized or planned.

## Phase 6 UI Implementation Sequence — 2026-08-10

1. Execute the route-by-route reconciliation in `phase6-contract-matrix.md`: exact DTO/Page/Problem shapes, documented model-profile operations, protected cursors, persisted replay/conflict receipts, strict idempotency keys, optimistic versions, real Job Run child projections, and focused backend endpoint tests. Do not accept dual wire shapes.
2. Correct the typed web client to the reconciled contract and test every consumed route, headers/body/status, typed Problem errors, pagination, conflicts, and secret non-echo behavior.
3. Add the named reusable product components and state matrices in `phase6-contract-matrix.md`, isolated stories, focused accessibility/keyboard/state tests, responsive fallbacks, and design-map entries.
4. Compose Chat, Jobs, Accounts, Plugins, Memory, Activity, and Settings routes from those components; retire parallel legacy product bindings and move operator-only links under Settings/Admin.
5. Add Playwright transport simulations for Journeys B, D, E, F/H, I, and J, including Action terminal/reconciliation states and blocked recovery; run all requested gates and update only evidence-backed R2 report/matrix claims.
