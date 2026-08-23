# ADR 0038: Trusted State Projection and Context Release

## Status

Accepted for the Tessera 3.0 foundation.

## Context

Tessera already persists owner-scoped Evidence and Assertions, atomic
Corrections, explicit conflicts, and deterministic context envelopes. The 3.0
roadmap requires those records to become a worker-independent Trusted State
surface while keeping context disclosure under Tessera policy.

## Decision

Trusted State is a derived, explicit-key projection in `Tessera.Core.Kernel`.
It uses the existing assertion-history and evidence repository ports and does
not add a table, migration, ontology, search index, or persisted projection.

Context assembly is not authorization. `ContextReleaseService` constructs the
fixed `read:context` policy request, binds it to the verified delegated owner,
records the decision, and performs no state read unless policy allows it. An
allowed release maps only current and conflicted Assertions into the existing
deterministic `ContextBuilder`; sensitivity and byte-budget omissions remain
explicit.

Authority is categorical and separate from confidence. Legacy source
Assertions remain `UnclassifiedSource`; they are not silently promoted to
provider-authoritative state. Corrections append predecessor lineage and retain
the superseded Assertion.

## Consequences

- Current state, history, provenance, Corrections, and conflicts remain durable
  across worker or model replacement.
- Disclosure is owner-bound, policy-gated, auditable, deterministic, and
  bounded by explicit keys and item/byte limits.
- Context identity includes sorted provenance references, so changing the
  supporting Evidence or correction lineage changes the deterministic ID even
  when the rendered value is unchanged.
- Explicit-key reads may be N+1 and are not a cross-key transactional snapshot.
  Add a batch persistence API only after measured scale or consistency failures.
- No HTTP endpoint, UI, worker bridge, Responsibility, Action, dependency, or
  schema change is introduced by this decision.

## Rollback

Remove the projection, release service, tests, and this ADR. Stored data and the
existing R2 API remain unchanged because no migration or persistence contract
changed.