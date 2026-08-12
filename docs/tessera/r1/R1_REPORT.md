# Tessera R1 Report

## Status

**COMPLETE** - all 26 acceptance criteria, deterministic gates, and final product,
architecture, and security adversaries pass.

## R0 verification

R0 was verified before implementation at HEAD
`723aa31514ed840678bb01aa6b316ea0cfd10902`, branch `2.0-beta`. The intentionally
uncommitted R0 tree was preserved. Fresh entry gates matched the handoff, with no
unresolved Critical/High trust issue blocking a synthetic read-only R1 slice.

## Continuity vertical selected

Provider-neutral Follow-up continuity using a workflow-specific `FollowUp` aggregate
and deterministic local source fixtures.

## Why this vertical

Incomplete later evidence such as "Monday instead works for it" and "Sent it to
Rowan" has no safe stateless interpretation. Follow-up continuity gives accepted and
corrected Tessera state an observable job without a provider, cloud model, or action.

## What was built

- provider-neutral source record, local adapter, and deterministic parser;
- workflow-specific aggregate with candidate/current/conflict/history state;
- per-field evidence, parser/version, confidence, correction, and lineage;
- acceptance, correction, conflict resolution, stale/replay protection, and context;
- additive SQLite v3/v4 and transactional owner-scoped repository;
- authenticated Broker API with typed fail-closed errors;
- Attention, Tracked, Detail, Timeline, Why, and Correct UI;
- visibly volatile no-backend preview and desktop/phone browser proof.

## What was deliberately not built

No provider connector, credentials, cloud model, graph/vector store, semantic search,
generic ontology/agent, chat, external action, autonomous retry, production lifecycle
guarantee, or permanent product-category claim.

## User-visible continuity demonstrated

The user accepts an initial candidate, corrects the deliverable, observes a context-
resolved schedule update, accepts it, sees incompatible Friday evidence as conflict,
resolves it, observes completion, and inspects exact Timeline and Why data.

## Example state timeline

1. Initial evidence creates three candidates; acceptance makes them current.
2. Correction supersedes `lease checklist` with `lease renewal checklist`.
3. Monday evidence uses accepted context to propose `2026-08-17`; acceptance replaces Friday.
4. New Friday evidence creates explicit conflict; resolution restores `2026-08-17`.
5. Sent evidence uses corrected context to propose completion; acceptance completes it.

## Example Why? provenance

Completion points to `evidence:local.fixture:r1-sent`, parser version, source time,
confidence, and exact corrected-deliverable/Rowan revision IDs. Resolution points to
user evidence and both Monday and conflicting-Friday revisions.

## Example correction

`lease checklist` becomes superseded history. `user.correction` evidence creates the
current `lease renewal checklist`, survives restart/replay, and is reused later.

## Example conflict

Credible newer Friday evidence changes both due-date values to `Conflicted`; no current
due date is returned until explicit resolution preserves both lineages.

## Compounding-memory result

**PASS for mechanics.** Stateless Monday/Sent extraction returns `NeedsContext`.
Persisted accepted/corrected state resolves both, survives current/candidate/conflict
restarts, prevents stale resurrection, and answers changes without replaying sources.
This does not establish market demand.

## Provider coupling assessment

Canonical state uses provider-neutral source identity, timestamps, hashes, sensitivity,
revisions, and provenance. No provider schema or credential is required.

## Model coupling assessment

No model is used. The parser records its version and creates candidates only. Accepted
state is Tessera-owned and remains valid if extraction code is replaced.

## Tests and gates

- 617 backend tests passed; 94 web tests and strict production build passed.
- 16 Playwright tests passed across desktop and 390px phone projects.
- Storybook build, ESLint, kubeconform 4/4, and deployment secret scan passed.
- Production npm and direct/transitive NuGet audits found no vulnerabilities.
- Architrave run validation, diff hygiene, and editor diagnostics passed.

## Adversarial findings

### Fixed

Strict UTC, locator validation, replay result version, supersession timestamp drift,
selective-acceptance causality, Why fallback, invalid correction choices, full UI
journey, exact Timeline/Why proof, focus/Escape, reduced motion, responsive behavior,
truncation, and preview durability disclosure.

### Remaining

Non-blocking limits: no forced simultaneous two-connection race test, complete DLP,
live ingestion, market validation, or production lifecycle guarantees. Final product,
architecture, and security verdicts are PASS.

## Files changed

R1 adds FollowUp Core/source/service contracts, SQLite persistence/migrations, Broker
API/composition, backend tests, continuity React client/hooks/page/components/stories/
tests, Playwright coverage, design-map/spec updates, required R1 docs, and run evidence.
Existing R0 trust changes remain part of the uncommitted tree.

## Commits

No commit or branch was created.

## New settled decisions

FollowUp is the R1 proof vertical only; candidate output is not truth; correction and
resolution are evidence; consequential fields require provenance; supersession time is
evidence-causal; local composition is explicit/fail-closed and demo state is volatile.

## Decisions intentionally left open

Provider, production ingestion, model, semantic retrieval, graph/vector, broader
ontology, production persistence lifecycle, external execution, pricing, and permanent
product category.

## Recommendation

Continue this vertical only for real-user discovery and a safe read-only source pilot.
Do not begin R2 knowledge evolution or execution yet. First establish recurring user
pain and design the privacy/deletion/backup contract for real personal evidence.