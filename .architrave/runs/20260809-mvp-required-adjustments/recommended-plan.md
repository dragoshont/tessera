# Recommended Plan

## Summary

Use appointment continuity as a conditional experiment, not a presumed product. Validate pain manually, repair trust boundaries, build only EvidenceRecord/Appointment/AppointmentRevision/Correction, prove compounding read-only value, then decide whether to execute.

## Implementation Sequence

1. Product-owner sign-off on the revised scope.
2. Phase -1 reality study and `GO/PIVOT/STOP`.
3. Phase 0 trust repairs before any active ingestion.
4. Durable read-only core and concrete backup/erasure.
5. Microsoft read connector with selected-folder and ICS boundaries.
6. Read-only MVP and event-count/compounding gates.
7. Mandatory product review.
8. Only after `CONTINUE`: MVP+1 calendar execution.

## Test Strategy

Separate detection, field extraction, correlation, temporal state, provenance, correction, product value, and compounding metrics. Use sender/thread-separated holdout data. Test ICS-only attachments, no remote model calls, restart, erasure, and trust boundaries. MVP+1 adds ownership, approval, ETag, timeout, and verification tests.

## Rollback / Recovery

- Phase -1 has no active connector or Tessera backup.
- Phase 0 blocks ingestion merges until trust tests pass.
- MVP has no write scope, so rollback cannot mutate calendar state.
- Restore fails closed without the external erasure journal.
- MVP+1 remains disabled unless product review returns `CONTINUE TO EXECUTION`.

## Human Approval Needed

The 20 sign-off decisions in `docs/product-mvp-audit.md`, Phase -1 `GO`, encrypted PVC/backup plan, Graph app/consent, local-model approval if used, and separate MVP+1 write enablement.
