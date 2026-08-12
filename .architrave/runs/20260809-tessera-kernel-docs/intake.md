# Intake

## Understanding

Create the requested Tessera Kernel v1 architecture and handoff documents only. The documents must describe an internal provider-neutral R0 foundation, preserve the existing broker as the trust/execution module, and remain subordinate to Phase -1 and the read-only appointment MVP.

## Acceptance Criteria

1. Create `NORTH_STAR.md`, `ARCHITECTURE.md`, `DOMAIN_MODEL.md`, `SECURITY_BOUNDARIES.md`, `MEMORY_AND_KNOWLEDGE.md`, `EXECUTION_MODEL.md`, `CAPABILITY_MODEL.md`, `TEST_MATRIX.md`, `ROADMAP.md`, `DECISION_LOG.md`, `ADVERSARIAL_SECURITY_REVIEW.md`, `ADVERSARIAL_ARCHITECTURE_REVIEW.md`, and an honestly in-progress `OVERNIGHT_REPORT.md`.
2. Create `adr/ADR-001-tessera-modular-monolith.md`, `adr/ADR-002-durable-state-owned-by-tessera.md`, and `adr/ADR-003-agents-and-models-are-replaceable-workers.md`.
3. Do not modify the existing `OVERNIGHT_BASELINE.md` or `REPOSITORY_MAP.md`.
4. Define Core contracts, a SQLite adapter boundary, the existing broker trust module, and inward dependency direction while preserving `Tessera.Core` as the dependency-free root.
5. Keep credentials, grants, bindings, broker security audit, and raw prompts outside Kernel product-state persistence.
6. Define principal, evidence, event, assertion, action, context, and capability contracts without provider, cloud-model, or live-write commitments.
7. Constrain generic Assertion to internal current-state infrastructure; it is neither an ontology nor a user-facing product model.
8. Keep product decisions, Phase 0 trust blockers, proposed test mappings, and provisional implementation status explicit.
9. Cover owner scope, hostile evidence, prompt-injected authority, invalid principals, assertion conflict/supersession, context disclosure, action replay/payload swap/invalid transitions, capability version/side-effect policy, restart/migration, and secret-safe audit in the proposed test matrix.
10. Do not claim unobserved code, tests, reviews, migrations, backups, erasure behavior, or runtime evidence.
11. Keep changes within `docs/tessera/**` and `.architrave/**`; pass required-content checks, `git diff --check`, run-artifact validation, and two independent semantic reviews; do not commit.

## Grounding Sources

- `/Users/dragoshont/Downloads/TESSERA_VSCODE_OVERNIGHT_BUILD_SPEC.md`
- `architrave.config.json`
- `docs/product-mvp-audit.md`
- `docs/tessera/OVERNIGHT_BASELINE.md`
- `docs/tessera/REPOSITORY_MAP.md`
- Current `src/**`, `tests/**`, `docs/architecture.md`, and `docs/adr/**`
- `knowledge/yagni.md` and `knowledge/learning-loop.md`

## Assumptions

- Test mappings distinguish observed executable tests from required future product/provider tests.
- `OVERNIGHT_REPORT.md` reports this documentation slice and the overall overnight run as in progress; it does not claim final gate or independent-review completion.
- This documentation-only request does not authorize code, manifest, deployment, external-service, or runtime changes.
- Concurrent implementation files are user-owned work. This run will not edit or revert them and will describe only source-observed behavior.

## Current Implementation Observation

The user reports the concurrent Kernel and trust work is now integrated and its focused tests pass. Source inspection confirms Core Kernel contracts, the SQLite adapter and migrations, and the requested trust fixes are present. `KernelMigrations` has no dedicated prompt, model-output, diagnostics, credential, policy, or security-audit columns; `SqliteMigrationTests.Schema_contains_product_state_only` enforces those structural exclusions, not arbitrary-content rejection in generic fields. `IActionAuthorizationRepository.TryConsumeAndAuthorizeAsync` requires authorization consumption and `PROPOSED` to `AUTHORIZED` reservation in one transaction, and the SQLite implementation commits both together or rolls back.

Source inspection also confirms named writes remain held despite a caller-controlled legacy `confirmed` flag, portal-created credential references are server-generated, portal mutations replace the resolver's live immutable binding snapshot, new portal bindings use canonical principal IDs while old email/username matching is legacy compatibility only, and live-view messages require the expected origin and iframe source. Final reporting will record exact gate results observed after documentation is written; it will not borrow the user's focused-test result as a final full-gate result.

## Scope Reconciliation

The canonical overnight specification explicitly authorizes R0 contracts for `PrincipalRef`, `EvidenceRecord`, `ObservationEvent`, constrained `AssertionRecord`, `ActionRecord`, `ContextEnvelope`, workers, capabilities, and SQLite persistence. Its own adversarial review calls generic Assertion "ontology by stealth" and limits it to supported current-state infrastructure: no graph store, ontology hierarchy, recursive reasoning, implicit traversal, or automatic graph population.

The product audit remains controlling for product behavior. Phase -1 still decides whether appointment continuity should proceed; the read-only MVP uses `Appointment`, `AppointmentRevision`, and field-level provenance; generic Claim, Entity, Situation, Commitment, ontology, and semantic retrieval remain product-gated. R0 documentation therefore records an internal compatibility seam, not a validated or user-facing world model.

## Canonical Spec Matrix

| Documentation criterion | Canonical source | Repository evidence |
|---|---|---|
| R0 is the smallest durable kernel, not the mature product | Overnight spec §§11, 31, 49; product audit §§2-6 | Baseline and report status language |
| Assertion is constrained current-state infrastructure, not ontology | Overnight spec §§19.4, 49 Attack 2; product audit §§3, 17 | `Kernel/Assertions.cs` plus product-gated docs |
| Broker trust assets are preserved as a module | Overnight spec §§7, 15 Employee B, 49 Attack 3 | `REPOSITORY_MAP.md`, Broker/Core boundaries |
| Models and agents are replaceable workers | Overnight spec §§6.4-6.6, 23 | `Kernel/Intelligence.cs`, fake-adapter test |
| Kernel schema has no dedicated secret or raw/model execution columns | Overnight spec §§19.2, 28; prior judge finding | `KernelMigrations.cs`, schema exclusion test; content validation remains a future gate |
| Exact one-time authorization cannot be caller-issued or crash-split | Overnight spec §§25-27, AC-04, AC-14, AC-15 | Provider write hold; transactional SQLite consume/reserve |
| Product scope remains Phase -1 then read-only appointment MVP | Product audit §§2-6, 15, 18 | Roadmap and decision constraints |

The canonical specification lives outside the repository at `/Users/dragoshont/Downloads/TESSERA_VSCODE_OVERNIGHT_BUILD_SPEC.md`; this matrix persists the criteria used by this run without copying the full external document.

## Blocking Questions

None.
