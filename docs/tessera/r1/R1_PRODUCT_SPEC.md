# Tessera R1 Continuity Product Spec

## Product Claim

Tessera can preserve accepted, corrected FollowUp context so deterministic later
evidence is interpreted more accurately after restart, while exposing exactly why
each consequential field is current, candidate, conflicted, superseded, or rejected.
Synthetic fixtures prove mechanics, not market frequency or production ingestion.

## Acceptance Criteria

The normative checklist is AC-R1-01 through AC-R1-26 in
`.architrave/runs/20260810-r1-continuity/intake.md`. `R1_TEST_MATRIX.md` maps every
criterion to executable or review evidence.

## Discriminating Journey

1. Import “I will send the lease checklist to Rowan by 2026-08-14.” and accept it.
2. Correct the deliverable to “lease renewal checklist.”
3. Import “Monday instead works for it.”; accepted context resolves `it` and Monday.
4. Accept the due-date candidate, restart the store, and recover identical state.
5. Import newer credible evidence that the old Friday deadline still stands.
6. Show explicit conflict and resolve it to 2026-08-17 with user evidence.
7. Import “Sent it to Rowan.”; corrected context resolves the completed object.
8. Accept completion and inspect ordered Timeline and field-level Why.

## Local API Contract

All routes use the existing portal authentication boundary. The server derives the
owner from the verified canonical principal, or the existing loopback-only dev
principal. No request accepts an owner identifier.

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/portal/continuity/follow-ups?view=attention|tracked` | Owner-scoped list |
| `GET` | `/portal/continuity/follow-ups/{id}` | Detail, revisions, timeline |
| `GET` | `/portal/continuity/follow-ups/{id}/why` | Field-level source and lineage projection |
| `POST` | `/portal/continuity/fixtures/{fixtureId}/import` | Import one deterministic local fixture |
| `POST` | `/portal/continuity/follow-ups/{id}/accept` | Accept candidate revisions |
| `POST` | `/portal/continuity/follow-ups/{id}/correct` | Correct one consequential field |
| `POST` | `/portal/continuity/follow-ups/{id}/resolve` | Resolve one explicit conflict |

Import receives `{ operationId, followUpId?, expectedVersion? }`. `initial` forbids a
FollowUp ID and creates one deterministically. Every contextual fixture requires an
exact owner-scoped FollowUp ID and expected version; absent/multiple implicit matches
are never searched. Mutation bodies contain `operationId` and `expectedVersion`;
correct/resolve also contain `field` and `value`. Accept may contain candidate revision
IDs; an omitted list means all currently visible candidates.

An operation ID is unique per owner and stores a request hash plus resulting FollowUp
ID/version. Exact replay returns that result with `replayed: true`; reuse with a
different payload returns `409`. Source replay has the same behavior through the
processed source identity. Version mismatch returns `409` without mutation.

Common responses use `{ code, message }`: `400` invalid input, `401` unauthenticated,
`404` absent or other-owner object, `409` stale version/invalid state/idempotency
collision, `422` contextual fixture without a usable accepted context, and `503`
local continuity storage not composed. Cross-owner existence is not disclosed.

## DTO Shape

Lists return at most 100 rows ordered by `updatedAt DESC, followUpId ASC` and include
`truncated`. Detail returns at most 100 timeline rows ordered by monotonic sequence and
includes `timelineTruncated`. The detail DTO contains aggregate identity/status/version/
timestamps, field revisions, and timeline entries. Each revision includes ID, field,
value, state, evidence references, source timestamp, parser version, confidence,
correction evidence reference, and superseded/conflicting revision references.
Timeline entries include sequence, kind, field, summary, evidence reference, source
timestamp, and recorded timestamp. Why returns these source-grounded facts grouped by
field, never a generated explanation.

Mutation success is `{ followUpId, version, replayed }`; clients refetch detail. IDs
are 1-128 visible ASCII characters. Operation IDs are 1-128 characters. Deliverable
is 1-256 characters, counterparty 1-128, `dueAt` is strict `yyyy-MM-dd`, and
`completedAt` is an ISO-8601 UTC instant. Only the four named fields are accepted.

## UI Surfaces

- **Attention:** candidates and conflicts requiring a decision.
- **Tracked:** accepted active and completed FollowUps.
- **Detail / Timeline:** current values plus ordered transitions and history.
- **Why:** evidence timestamp, parser, confidence, and correction/supersession chain.
- **Correct:** explicit field/value correction with current value and effect visible.

The portal reuses design-map `AppShell`, `ActivityFeed`, `ConnectionDrawer`, and
`PendingWritesTable` anatomy. Before route/API integration it creates and tests a
`Continuity/FollowUpWorkspace` Storybook story for every required state, then adds
the real story/component to the design map. It uses the existing shell, tabs, badges,
tables, sheets/dialogs, alerts, skeletons, and CSS tokens in `web/src/index.css`.
Candidate uses amber plus `Clock`; conflict uses accent plus `TriangleAlert`; current
uses green plus `CircleCheck`. Every state has a text label and border/icon cue. Red
remains reserved for true errors.

The table reflows to labeled rows below the desktop breakpoint. Dialogs trap focus,
Escape closes, and focus returns to the invoking Correct button. Mutation controls
disable during submission; stale `409`, auth loss, unconfigured `503`, and request
races show explicit alerts and never replace fresher data. Controls meet 24px minimum
targets, retain visible focus, use semantic labels, and honor reduced motion. There is
no chat and no external execution control.

Storybook/component tests run axe with zero serious/critical violations; verify
semantic heading/table/list/dialog structure and accessible names; exercise the full
keyboard order, correction open/submit/cancel/Escape and focus return; assert state
text/icons without color; and inspect computed light/dark contrast at WCAG 2.2 AA.
Responsive tests cover desktop table and narrow labeled-row layouts. Animation is
disabled under `prefers-reduced-motion`.

## Capability Boundaries

R1 has no live provider ingestion, provider credentials, cloud model, vector/graph,
automatic execution, external writes, deletion/backup guarantee, deployment/PVC
durability claim, or permanent Tessera product-category decision.
