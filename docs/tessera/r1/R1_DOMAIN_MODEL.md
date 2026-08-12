# Tessera R1 FollowUp Domain Model

## Scope

R1 proves continuity with one workflow-specific `FollowUp` aggregate. It does not
introduce a generic Commitment, Situation, Entity, Claim, graph, vector store, model
provider, or agent-owned state transition.

## Aggregate

`FollowUp` is owner-scoped by canonical `PrincipalRef.PrincipalId` and contains:

- `followUpId`, `ownerPrincipalId`, `status`, `createdAt`, `updatedAt`, and optimistic `version`;
- field revisions for `deliverable`, `counterparty`, `dueAt`, and `completedAt`;
- an ordered, bounded transition timeline.

Aggregate status is `Attention`, `Tracked`, `Conflict`, or `Completed`. Field revision
state is `Candidate`, `Current`, `Conflicted`, `Superseded`, or `Rejected`. Candidate
and current are separate records. At most one current revision exists for each field;
a conflicted field has two or more visible conflicted revisions and no silent winner.

## R0 Primitive Reuse

Every imported or user-originated transition persists:

1. an owner-scoped `EvidenceRecord`;
2. an append-oriented `ObservationEvent`;
3. field-specific `AssertionRecord` values for extracted/current/history state;
4. a `ContextEnvelope` only when incomplete evidence needs accepted prior context.

Assertions remain constrained Kernel infrastructure. The user-facing and invariant-
owning model is `FollowUp`.

## Invariants

1. Owner identity comes from the authenticated boundary, never request content.
2. Extraction can create candidates, rejected stale revisions, or explicit conflict;
   it cannot silently create current state.
3. Acceptance, correction, and conflict resolution require user evidence and an
   exact expected aggregate version.
4. Correction supersedes a current revision and records both correction evidence and
   the prior revision lineage.
5. Credible incompatible evidence marked by the deterministic grammar creates
   conflict; neither value wins until explicit resolution.
6. A source identity is processed once per owner. Replay returns its original result.
7. Source evidence older than the accepted field source timestamp is retained as
   rejected history and cannot resurrect state.
8. Completion is a candidate until accepted and performs no external action.
9. Assertion supersession time is immutable and causally bound to the descendant's
   exact acceptance/correction evidence, never the aggregate's latest update time.

## State Transitions

```text
fixture import -> Attention(candidate fields)
Attention --accept--> Tracked(current fields)
Tracked --correct--> Tracked(new current + superseded history)
Tracked --contextual import--> Attention(candidate revision + existing current)
Attention --accept--> Tracked(revised current + superseded history)
Tracked --incompatible evidence--> Conflict(conflicted revisions)
Conflict --resolve--> Tracked(resolution current + preserved conflict lineage)
Tracked --completion import--> Attention(completion candidate)
Attention --accept completion--> Completed
```

## Persistence Boundary

`IFollowUpRepository` stores one complete transition atomically: aggregate metadata,
field revisions, timeline entry, processed source identity, and the corresponding R0
evidence/event/assertions. SQLite is the R1 adapter. No credential, grant, secret,
prompt, model output, or external action enters these tables.

SQLite migration v3 adds `follow_ups`, `follow_up_revisions`, `follow_up_timeline`,
`follow_up_sources`, and `follow_up_operations` with owner-leading primary/foreign
keys. Migration v4 additively binds processed source receipts to a complete normalized
payload hash. Initialization applies each migration and its marker in one transaction; a failed
transaction rolls back and a later initialization retries. Existing v1/v2 tables are
not altered or backfilled. Prior binaries ignore v3/v4 state. Application rollback is
therefore code-only; dropping R1 tables is destructive and requires an operator-owned
database backup outside R1.
