# Recommended Plan

## Summary

Produce a coherent R0 documentation set that separates observed repository truth from proposed Kernel contracts and from product-gated work.

## Implementation Sequence

1. Verify current projects, trust seams, tests, and governing ADRs. Exit: every required claim is tied to current source, an executable test, the canonical overnight spec, or the controlling product audit.
2. Write `NORTH_STAR.md`, `ARCHITECTURE.md`, `DOMAIN_MODEL.md`, and ADR-001 through ADR-003. Exit: status vocabulary, module ownership, dependency direction, contract semantics, and decision consequences agree.
3. Write `SECURITY_BOUNDARIES.md`, `MEMORY_AND_KNOWLEDGE.md`, `EXECUTION_MODEL.md`, and `CAPABILITY_MODEL.md`. Exit: trust, persistence, provenance, disclosure, action, and capability invariants have explicit non-goals and failure states.
4. Write `TEST_MATRIX.md`, mapping each R0 invariant to an observed test project and a proposed unit, persistence, boundary, or adversarial check. Exit: every critical contract has a positive and failure-path check, with implementation status explicit.
5. Write `ROADMAP.md` and `DECISION_LOG.md`. Exit: R0 does not bypass Phase -1/Phase 0; provider/cloud/live-write/product decisions remain open; migration, backup, restore, and erasure obligations are staged honestly.
6. Create both adversarial review files as honest placeholders marked pending independent reviewer findings; do not fabricate reviewer results.
7. Write `OVERNIGHT_REPORT.md` as `IN PROGRESS`, then run required-content, path-scope, whitespace, repository, artifact, and two-family semantic gates without changing the report to final unless those gates actually complete. Review candidate lessons, promotion threshold, stale-fact validation, and redaction; record a no-change disposition when no durable repeated lesson is established.

## Contract Invariants To Document

- Dependency direction: `Tessera.Core.Kernel` owns dependency-free records, state machines, and ports. The integrated `Tessera.Persistence.Sqlite` adapter depends inward on Core. Future Broker runtime composition may depend on both; Core never references Broker, SQLite, provider, cloud, or HTTP types.
- Persistence ownership: Kernel schema contains Kernel state and product provenance tables only, with no dedicated credential, grant/binding, security-audit, raw-prompt, model/worker-output, diagnostics, or secret columns. Generic text/JSON fields require producer validation; content-level exclusion remains a future adversarial gate.
- Broker boundary: no existing broker route is integrated merely because contracts exist. The existing Broker trust module alone authenticates callers, evaluates broker policy, obtains out-of-band human approval, and emits security audit. Future broker composition must supply the trusted issuance path before Kernel authorization can enable a live side effect; missing/malformed identity, stale authorization, owner mismatch, persistence conflict, unknown capability, and invalid transition fail closed.
- Core authorization boundary: Core defines the immutable authorization record, exact binding predicate, one-time-consumption port, and action transition rules. `ActionAuthorizationService.IssueAsync` is currently a mechanical R0 contract with no trusted caller boundary; it is not evidence of broker-integrated issuance. Core must not authenticate callers, evaluate broker grants, treat caller/model claims as approval, access credentials, or claim security-audit completion.
- Authorization binding: a one-time authorization binds canonical principal, action ID, capability ID/version, canonical payload digest, target scope, and issue/expiry. Worker/model claims never issue or satisfy authorization. Consumption is valid only when it atomically reserves the exact matching `PROPOSED` action as `AUTHORIZED` in the same transaction.
- Action consistency: the action owns a stable idempotency key. State transitions use expected-version/optimistic concurrency. `EXECUTION_SUCCEEDED`, `PROVIDER_VERIFIED`, and `EXTERNALLY_CONFIRMED` remain distinct. R0 exposes `RECONCILIATION_REQUIRED`, but failure classification does not yet prove every timeout/ambiguous result enters it; provider integration must close that future gate before live writes.
- Capability boundary: descriptors are versioned and declare side-effect class, permissions, data classes, idempotency, and verification support. Invocation request/response and errors are structured; provider-specific payloads and receipts remain adapter data, not Core types.
- Migration and recovery: the integrated adapter uses schema-versioned forward migrations, clean bootstrap, and deterministic repeatability. Pre-migration backup, restore verification, and fail-closed startup on unknown/newer schema remain future product/deployment gates. Destructive contract steps require a separately approved expand-migrate-contract phase and tested recovery; R0 docs claim no implemented backup or complete erasure.
- Compatibility: persisted record schema versions and capability versions are explicit. Additive readers may accept known older versions; unknown versions fail closed. Contract changes update ADR/test mappings before integration.
- Audit: product provenance and security audit correlate by opaque IDs but remain separate. Their contracts prohibit credentials, tokens, secret values, and unnecessary evidence content; content-level adversarial coverage remains a future gate where generic fields exist.

## Test Strategy

- Source inventory: confirm the current Kernel namespace, SQLite adapter, migrations, test project, and trust fixes without editing them.
- Architecture: check that Core has no outward adapter dependency and Kernel persistence excludes credentials, grants, bindings, and security audit.
- Identity/data: proposed checks cover missing issuer/tenant/subject, same display name across tenants, cross-owner reads, duplicate/hostile evidence, hash mismatch, and unsupported provenance.
- Knowledge/context: proposed checks cover supersession, explicit conflict, user assertion versus inference, prompt-injected authority, sensitivity omission, stale state, and deterministic context reproduction.
- Execution/capability: proposed checks cover payload swap, approval replay, invalid transition, restart at `STARTED`, unknown outcome, version downgrade, undeclared side effects, and fake provider verification.
- Persistence/privacy: proposed checks cover clean schema, forward migration, repeatability, transaction failure, restart, optimistic conflict, retention state, restore fail-closed, and no untested complete-erasure claim.
- Documentation: required files/concepts, relative links, status-label drift, changed-path allow-list, `git diff --check`, and `harness/validate-run.sh`.
- Gates: run configured web/backend gates because the repository profile requires them, even though documentation changes should not affect code; record exact results without borrowing baseline counts.
- Semantic: independent Copilot/GPT-family and Claude-family reviews grade proposal and final documentation.

## Proposed Test Traceability

| Invariant | Status | Target and evidence | Expected result |
|---|---|---|---|
| Canonical identity | OBSERVED, PASS in full gate | `Tessera.Core.Tests/Kernel/PrincipalRefTests`; `ResolverTests.Canonical_binding_does_not_match_same_email_in_another_tenant` | immutable identity differs across tenant; display hint is non-authoritative |
| Owner scope | OBSERVED, PASS in full gate | `SqliteStatePersistenceTests.Queries_and_writes_are_owner_scoped` | cross-owner query/write fails closed |
| Assertion history | OBSERVED, PASS in full gate | `DomainSemanticTests.User_correction_supersedes_inference_without_erasing_provenance`; persistence equivalent | old state remains historical with provenance |
| Conflict honesty | OBSERVED, PASS in full gate | `DomainSemanticTests.Incompatible_assertions_become_explicitly_conflicted`; persistence equivalent | neither value wins implicitly |
| Context disclosure | OBSERVED, PASS in full gate | `ContextCapabilityTests.Context_filters_sensitivity_records_omission_and_is_reproducible` | restricted payload absent; omission present |
| Hostile evidence/prompt authority | FUTURE GATE for integrated execution path | future Core/Broker adversarial test | evidence remains data and cannot mutate policy, issue approval, or invoke a capability |
| Invalid principal and cross-tenant substitution | OBSERVED, PASS in full gate | `PrincipalRefTests`; canonical resolver test | malformed identity is rejected; same display email across tenants does not match |
| Binding and one-time consumption | OBSERVED, PASS in full gate | `DomainSemanticTests.Authorization_is_exact_expiring_and_one_time`; `SqliteExecutionPersistenceTests.Authorization_binding_and_consumption_survive_restart` | mismatch, expiry, and reuse fail closed; exact action becomes authorized |
| Payload swap/action replay | OBSERVED, PASS in full gate | authorization tests; immutable action binding and duplicate-idempotency tests | swapped payload and duplicate action identity fail closed |
| Invalid transition and verification honesty | OBSERVED, PASS in full gate | `DomainSemanticTests.Action_rejects_invalid_transition_and_keeps_execution_distinct_from_verification` | invalid transition rejected; execution success does not imply verification |
| Atomic authorization rollback | SOURCE-OBSERVED; dedicated fault test FUTURE GATE | transactional `TryConsumeAndAuthorizeAsync` | consume and action reservation commit together or both roll back |
| Trusted broker issuance | FUTURE GATE before live writes | future Broker integration test | caller/model claim cannot issue approval; broker policy plus out-of-band approval is required |
| Action recovery | PARTIAL OBSERVED; timeout classification FUTURE GATE | restart/idempotency tests and `RECONCILIATION_REQUIRED` state | no live retry policy until ambiguous outcomes are forced to reconciliation |
| Capability metadata | OBSERVED, PASS in full gate | `ContextCapabilityTests.Registry_distinguishes_versions_and_exposes_policy_metadata` | exact versions and side-effect metadata are visible; policy denial is a future integration duty |
| Schema safety | OBSERVED, PASS in full gate | `SqliteMigrationTests` | clean and v1-to-v2 migration are repeatable; prohibited tables/columns absent |
| Generic-field content leakage | FUTURE GATE | producer-validation and persistence adversarial tests | prohibited prompt/model-output/diagnostic/secret content is rejected before persistence |
| Audit secrecy | EXISTING BROKER COVERAGE; Kernel integration FUTURE GATE | broker audit tests plus schema exclusion test | no secret value or raw prompt/model output enters audit or Kernel schema |

Rows are labeled `OBSERVED`, `REPORTED PASS`, or `FUTURE GATE` from current evidence. Results are never inferred from file presence alone.

## Phase Exit Interpretation

Judge Gate 1 evaluates whether this plan is safe and sufficiently specified to begin writing documentation. Phase 2 artifacts and final repository gates are intentionally pending and are not proposal failures. Phase 2 cannot be called complete until those outputs and checks exist. Phase 3 cannot be called complete while configured gates fail, regardless of whether failures belong to concurrent work.

Phase 3 records one UTC-timestamped source/project/test inventory and treats it as the report's observation point. Later worktree movement is disclosed rather than folded into a stronger claim. The two protected existing files are verified against captured SHA-256 hashes. If concurrent source keeps a configured gate red, documentation artifacts may be reported as written and markdown-validated, but the overall run remains `PARTIAL/BLOCKED` with the exact unrelated failure and ownership recorded.

## Rollback / Recovery

All requested product artifacts are new files. Recovery is removal of only this run's new files; existing baseline, repository map, code, manifests, deployment, and product audit remain untouched.

No schema or migration is implemented in this phase. The documents require versioned forward migrations and clean/repeatable/restart tests for a later SQLite adapter. Product backup, restore, and erasure behavior remains gated by the deployment/product phase and may not be claimed from R0 interfaces alone.

## Human Approval Needed

No additional approval is required for this documentation-only phase. Any later code integration, provider choice, personal-source ingestion, external write, deployment, or runtime mutation requires its own approved phase.
