# Tessera Roadmap

## Controlling Sequence

The product MVP audit remains historical discovery evidence. The accepted R1 vertical
decision supersedes Appointment as the current proof slice only.

| Stage | Goal | Gate |
|---|---|---|
| Phase 0 | Trust reset before personal-source ingestion | All trust-boundary regressions and final review green |
| R0 | Provider-neutral Kernel/trust foundation | Durable semantics, migrations, adversarial gates; no product claim |
| R1 proof slice | FollowUp evidence, revisions, correction, conflict, Why, continuity | AC-R1-01..26 and compounding-memory gates |
| Product review | Decide whether execution is earned | Explicit `STOP`, `PIVOT`, or `CONTINUE TO EXECUTION` |
| Execution extension | Unselected; must be earned separately | Approval, idempotency, timeout, conflict, provider verification gates |

FollowUp is an R1 proof vertical, not Tessera's permanent product category.

## Current R0 Foundation

Source contains Core Kernel records/ports, SQLite v1/v2 schema and migrations, owner-scoped persistence, fake/deterministic worker and capability boundaries, durable action/workflow state, and the named trust fixes. This is architecture substrate, not a deployed provider workflow.

## Explicitly Not Chosen

- primary provider or cloud-model vendor;
- live external writes or autonomous action policy;
- graph/vector database or universal ontology;
- generic Claim, Entity, Situation, Commitment, Preference, or relationship model;
- iOS, offline sync, CloudKit, or production topology;
- production backup retention or complete-erasure guarantee.

## Earned Expansion

Add a richer structure only when a measured workflow failure requires it. Add semantic
retrieval only when structured current/history queries repeatedly fail. Add a provider
or live writes only after a separate product review and broker-integrated authorization/
reconciliation gates pass.

## Next Recommended Work

Run real-user discovery and design the privacy/deletion/backup contract required for a
safe read-only personal-source pilot. Do not infer provider or execution authorization
from the completed synthetic continuity proof.