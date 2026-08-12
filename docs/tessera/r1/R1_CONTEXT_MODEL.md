# Tessera R1 Minimum Useful Context

## Purpose

Context exists only to resolve the two incomplete fixture statements. It is not a
general retrieval or memory substrate.

## Construction

The import command must name the exact owner-scoped FollowUp for every contextual
fixture. The application service never searches for or ranks candidate aggregates.
It builds an R0 `ContextEnvelope` from current accepted revisions for that FollowUp.
It includes only:

- deliverable and counterparty for `Sent it to Rowan`;
- current due date plus deliverable/counterparty for `Monday instead works for it`.

Each `ContextItem` references the accepted revision evidence, uses the revision
source timestamp, and remains within a 2048-byte R0 context budget. At most three
items are built. Candidate, conflicted, rejected, and superseded values are excluded
from resolution context.

## Determinism

The parser receives the envelope as explicit input. With no matching accepted context,
the incomplete fixture returns `NeedsContext`; it does not create a FollowUp or guess.
Given identical source plus accepted context, the extraction result and context ID are
stable across restart.

## Privacy And Prompt Injection

No model or prompt consumes the context. The parser matches a fixed grammar and treats
source text as data. Unsupported instructions embedded in source content are ignored
as unsupported input. Context is owner-scoped and never assembled across principals.
