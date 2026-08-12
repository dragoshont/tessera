# Tessera R1 Source Contract

## Provider-Neutral Record

`SourceRecord` carries only:

| Field | Meaning |
|---|---|
| `sourceRecordId` | Adapter-local stable record identifier |
| `ownerPrincipalId` | Owner supplied by the trusted application boundary |
| `sourceType` | Provider-neutral adapter type, such as `local.fixture` |
| `sourceNativeId` | Stable native identity used for replay protection |
| `sourceLocator` | Non-secret diagnostic locator |
| `occurredAt` | When the source statement occurred |
| `observedAt` | When Tessera imported it |
| `content` | Bounded source text |
| `sensitivity` | R0 sensitivity class |

The adapter contract receives the authenticated owner and a source record ID. It
must not trust an owner embedded in fixture/provider content. It returns no credential
and performs no write outside Tessera.

## R1 Local Fixture Adapter

The deterministic adapter recognizes these synthetic records:

| Fixture | Source content | Parser outcome |
|---|---|---|
| `initial` | I will send the lease checklist to Rowan by 2026-08-14. | New FollowUp candidate fields |
| `monday` | Monday instead works for it. | Due-date candidate using accepted context |
| `conflicting-friday` | The Friday 2026-08-14 deadline still stands. | Explicit due-date conflict |
| `sent` | Sent it to Rowan. | Completion candidate using corrected context |

The records use UTC source timestamps `2026-08-10T09:00:00Z`,
`2026-08-11T09:00:00Z`, `2026-08-18T09:00:00Z`, and `2026-08-19T09:00:00Z`
respectively, with observed timestamps one minute later. Native IDs are
`r1-initial`, `r1-monday`, `r1-conflicting-friday`, and `r1-sent`. Content is
synthetic, non-secret, and capped at 4096 UTF-16 characters. The API does not accept
arbitrary source text in R1.

## Normalization

Import computes a SHA-256 evidence content hash and creates an R0 `EvidenceRecord` with the
adapter type/native ID/locator and deterministic parser producer/version. It creates
an `ObservationEvent` and extracted candidate assertions. `(owner, sourceType,
sourceNativeId)` is unique in R1 persistence. A separate replay-integrity hash binds
all normalized source fields, including owner, type/native ID, locator, UTC timestamps,
content, and sensitivity; replay returns the original aggregate without a second
transition. Source timestamps with non-zero offsets and secret-like locators fail closed.

## Deterministic Grammar

The parser version is `followup.fixture.v1`. Confidence is `0.99` for explicit initial
and conflict fields and `0.95` for contextual due/completion fields. The parser
supports only the four fixture sentence forms. Unsupported input is a typed
`Unsupported` result, not a guess. Contextual forms require the exact FollowUp named
by the import command and return `NeedsContext` when it is absent, other-owned,
conflicted, or lacks required current fields. Dates are UTC calendar dates; “Monday”
means the first Monday strictly after the statement timestamp.
