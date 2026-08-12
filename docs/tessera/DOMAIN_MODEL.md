# Tessera Kernel Domain Model

## Scope

These R0 records are internal infrastructure. They are not a user-facing product model
and do not establish a generic personal ontology. R1 adds a workflow-specific FollowUp
aggregate over them.

## Records

### PrincipalRef

Canonical owner identity derived from `(issuer, tenant, subject)`. `display_hint` is presentation metadata and never an authorization key.

### EvidenceRecord

Owner-scoped provenance anchor with source identity, timestamps, versioned content hash, retention state, sensitivity, producer, schema version, and optional bounded excerpt/reference. Evidence is data, not policy or approval.

### ObservationEvent

Append-oriented account of something observed or internally recorded. Corrections create new state and preserve prior events.

### AssertionRecord

Minimal evidence/lineage-backed state with subject key, predicate, value, assertion type, epistemic status, confidence, temporal bounds, producer, and schema version.

Allowed distinctions include user/source asserted, extracted, inferred, derived, and system-produced state; and candidate, supported, current, conflicted, superseded, and rejected status.

**Constraint:** generic Assertion is R0 internal current-state infrastructure only. It
is not product validation, ontology, graph edge, universal Claim, Entity, Situation,
Commitment, Relationship, or automatic inference substrate. R1 FollowUp remains
workflow-specific and field-provenanced.

### ActionRecord

Durable proposed/executed side effect bound to owner, capability ID/version, payload hash, target scope, policy reference, authorization reference, idempotency key, state, attempts, receipts, and verification metadata.

### ActionAuthorization

Short-lived, exact, one-time authorization bound to owner, action, capability/version, payload hash, target scope, issue time, and expiry.

### WorkflowCheckpoint

Versioned durable progress for long-running work: workflow identity/type, state, current step, input/output references, wake condition, timestamps, and optimistic version.

### ContextEnvelope

Deterministically derived task context containing permitted current facts, uncertain assertions, events/evidence references, omissions, and capability constraints under a size and sensitivity budget. It is not persisted canonical memory.

### CapabilityDescriptor

Stable capability ID/version plus input/output schemas, side-effect class, permissions, allowed data classes, idempotency support, and verification support.

## Semantic Rules

- Ingestion does not automatically create current belief.
- Consequential assertions require evidence or explicit derivation lineage.
- User correction supersedes without erasing provenance.
- Credible incompatible values become explicit conflict, not last-write-wins.
- Execution success, provider verification, and external confirmation are separate facts.
- All personal-state repository operations require canonical owner scope.