# Tessera Overnight Report

**Status:** R0 KERNEL COMPLETE — PRODUCT NOT VALIDATED
**Completed UTC:** `2026-08-09T22:04:35Z`

> **R1 supersession:** This is a historical R0 report. Its Appointment-next wording
> was superseded on 2026-08-10 by the workflow-specific FollowUp R1 proof decision;
> FollowUp is not declared Tessera's permanent product category.

## Executive Summary

Tessera now has a provider-neutral durable Kernel spine: canonical principals, evidence, append-oriented events, constrained assertions, deterministic context, replaceable model and capability contracts, durable action/workflow semantics, exact one-time authorization, and a SQLite adapter with explicit migrations. Existing broker trust paths were hardened without deploying or touching a live account.

This is architecture substrate, not the product MVP. Phase -1 and the read-only Appointment experiment in [the product audit](../product-mvp-audit.md) remain the next product step.

## Starting Repository State

- HEAD: `723aa31`
- Branch: `2.0-beta`
- Pre-existing untracked work: `.architrave/` and `docs/product-mvp-audit.md`; preserved.
- Baseline: 546 backend tests and 73 web tests passed.
- Full baseline evidence: [OVERNIGHT_BASELINE.md](OVERNIGHT_BASELINE.md).

## Implemented

- Dependency-free Kernel domain/contracts under `Tessera.Core.Kernel`.
- `Tessera.Persistence.Sqlite` with schema migrations v1/v2, WAL, foreign keys, busy timeout, indexes, owner-scoped repositories, optimistic versions, atomic observation/correction/authorization/execution transitions.
- Canonical human identity from issuer + tenant + subject.
- Evidence/event/assertion provenance and current/history/conflict semantics.
- Deterministic sensitivity/size-bounded context envelopes.
- Versioned capability descriptors, fake deterministic capability, and replaceable model adapter.
- Durable action state machine, workflow checkpoints, idempotency, provider/external verification distinction, exact payload/target/version binding, replay denial.
- Deterministic end-to-end fake Kernel scenario through restart and reconciliation.

## Security Fixes

- Caller-controlled `confirm=true` can no longer authorize named writes.
- Portal clients cannot choose credential-store keys; server allocates opaque references.
- New real-user bindings and portal/admin/audit/raw-approval/OAuth/MCP scope use canonical principal IDs.
- Tenant-aware signed users cannot fall back to legacy email grants/bindings.
- Portal bindings update the live resolver snapshot immediately.
- Live-view messages require the expected iframe window and origin.
- Authorization consume + `PROPOSED → AUTHORIZED` reservation is one transaction.
- Capability dispatch atomically reserves durable `AUTHORIZED → STARTED` and checks actual payload, target, version, owner, authorization, and idempotency.
- Lifecycle bypass, temporal inversion, hostile evidence authority, stale replay, payload/target swap, and obvious secret persistence have regressions.

Details: [ADVERSARIAL_SECURITY_REVIEW.md](ADVERSARIAL_SECURITY_REVIEW.md).

## Persistence Changes

- New additive tables: principals, evidence, observation events, assertions, actions, action authorizations, workflow checkpoints, and schema migrations.
- No credentials, grants, bindings, broker security audit, prompt/model output, diagnostics, token, password, or API-key columns.
- Migrations are forward-only and additive. A destructive rollback requires restoring a pre-migration backup.
- Kernel persistence is intentionally not composed into the deployed Broker; deployment has no approved persistent-volume/encryption/backup contract.

## Not Implemented By Design

- Real provider or personal-source ingestion.
- Cloud model or production AI call.
- Live Kernel external writes or autonomous retry.
- Graph/vector database, ontology, Situation/Entity/Commitment product model.
- Production PVC, encryption, backup, restore, forget, erasure, or multi-device sync.
- Product validation.

## New Tests And Gates

- Backend: **599 passed**, 0 failed.
- Web: **74 passed**, 0 failed; production build passed.
- Kubernetes render and kubeconform: 4/4 valid.
- Deployment secret scan: passed.
- NuGet direct/transitive vulnerability audit: no vulnerable packages.
- Editor diagnostics: clean.
- `git diff --check`: passed.
- Final source/test inventory: [FINAL_INVENTORY.md](FINAL_INVENTORY.md).

## Adversarial Findings

### Fixed

Payload binding, canonical identity collision/fallback, action lifecycle bypass, correction ordering, culture-sensitive context IDs, temporal inversion, hostile evidence authority, arbitrary credential references, caller self-confirmation, iframe spoofing, and obvious secret persistence.

### Residual / Deferred

- Legacy display-keyed policies need one-way migration; tenant-aware real users fail closed until migrated.
- Kernel authorization and live `WriteChallenge` must converge before live Kernel execution.
- Complete DLP and product deletion/backup guarantees are not claimed.
- OIDC `portal.admins` now requires canonical `principal:sha256:...` IDs.

Final independent architecture verdict: PASS with documented pre-live gates.
Final independent security verdict: PASS with no critical/high R0 blockers.

## Files Changed

- Added Kernel Core contracts, SQLite adapter, test project, and `docs/tessera/**` handoff set.
- Updated solution/package manifests.
- Hardened existing identity, grants/bindings, portal, egress/OAuth/MCP, audit, provider, and React handoff/connect paths.
- Exact file inventory: [FINAL_INVENTORY.md](FINAL_INVENTORY.md); use `git status --short` for the full trust-path diff.

## Commits

No branch or commit was created.

## Open Product Decisions

Email/provider choice, Phase -1 outcome, cloud model, graph/vector storage, trusted-edge sync, production deployment/backup, autonomous action policy, healthcare scope, sharing/multitenancy, and pricing remain open.

## Recommended Next Phase

Run Phase -1 Product Reality Study. Do not begin a provider connector or compose Kernel execution into the Broker until Phase -1 returns `GO` and the pre-live trust/PVC/backup gates are approved.

## Exact Human Verification Commands

```bash
PATH="/usr/local/share/dotnet:$PATH" ./gates/backend-checks.sh
./gates/checks.sh
PATH="/usr/local/share/dotnet:$PATH" dotnet list Tessera.slnx package --vulnerable --include-transitive
./harness/validate-run.sh .architrave/runs/20260809-tessera-kernel-docs
git diff --check
git status --short
```

No deployment apply, runtime mutation, secret access, or external account action was performed.