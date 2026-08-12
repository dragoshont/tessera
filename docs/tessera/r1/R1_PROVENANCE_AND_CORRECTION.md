# Tessera R1 Provenance And Correction

## Field-Level Provenance

Every consequential field revision persists:

- one or more evidence IDs;
- the source timestamp;
- deterministic parser/producer version;
- confidence in the range 0-1;
- revision state and creation timestamp;
- correction evidence ID when user-corrected/resolved;
- superseded or conflicting revision IDs.

The source timestamp comes from the `EvidenceRecord`; observed/recorded timestamps
remain separate. Why reads these records directly and does not synthesize rationale.

## Acceptance

Acceptance is user evidence, not a flag flip with no source. The operation creates a
bounded `user.acceptance` evidence record/event and transitions selected candidates
to current. Existing current revisions for the same field become superseded and are
linked from the accepted revision. Their `validTo`/`supersededAt` values derive from
the exact acceptance evidence attached to that descendant and do not drift when later
transitions update the aggregate.

## Correction

Correction creates `user.correction` evidence and a `UserAsserted` assertion. The
prior current revision becomes superseded; the new current revision references the
correction evidence and prior revision. The original extracted evidence remains
queryable in history.

## Conflict And Resolution

The deterministic grammar can label credible evidence incompatible with current
state. Import marks both values conflicted and sets aggregate status `Conflict`.
Resolution requires `user.resolution` evidence, exact expected version, field, and
chosen value. The resulting current revision references every conflicting revision.

## Replay And Staleness

Replay is rejected by stable source identity before extraction. For a field with
accepted state, source evidence whose source timestamp is not newer is retained as
`Rejected` history with a stale reason; it cannot change current/conflict state.
User correction/resolution ordering uses operation evidence and optimistic versions,
not source timestamp spoofing.
