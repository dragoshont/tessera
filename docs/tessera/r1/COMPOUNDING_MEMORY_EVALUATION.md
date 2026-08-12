# Tessera R1 Compounding Memory Evaluation

## Question

Does accepted, corrected state make later incomplete evidence deterministically more
useful after process restart than stateless parsing alone?

## Baseline

The fixed grammar cannot resolve “Monday instead works for it” or “Sent it to Rowan”
without an exact accepted FollowUp. Both return `NeedsContext`; no candidate is created.

## Continuity Result

| Evidence | Accepted context used | Result |
|---|---|---|
| Monday instead works for it. | Corrected deliverable, Rowan, current Friday due date | Candidate due date 2026-08-17 |
| Sent it to Rowan. | Corrected deliverable and Rowan | Completion candidate linked to corrected revision |

The context envelope is owner-scoped, accepted-only, at most three items, and 2048
bytes. Candidate, conflicted, rejected, and superseded values are excluded. Fresh
SQLite store instances recover current, candidate, and conflict states with identical
field provenance. The ordered timeline answers what changed without reprocessing the
source history.

## Compounding Evidence

The second contextual statement uses the corrected deliverable rather than the
original extracted value. This is the discriminating compounding result: user review
changes durable state, and that state changes a later deterministic interpretation.
Replay and stale evidence cannot erase the correction or resurrect Friday. Accepted
assertion supersession times remain tied to their own acceptance evidence through
later transitions.

The browser proof inspects the exact correction, Monday, conflict, resolution, and
completion timeline/evidence chains at desktop and phone widths. The compounding
result is user-visible, not only a repository invariant.

## Limits

**Result: PASS for mechanics.** This does not establish market frequency, model
quality, live-provider behavior, semantic retrieval need, or a permanent product
category. The corpus is intentionally synthetic and local.
