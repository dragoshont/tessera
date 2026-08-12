# Recommended Plan

## Summary

Use the existing broker as Tessera's trust/execution module and add a four-record appointment-continuity module. Start with one opt-in Microsoft 365 account, one selected Outlook folder, and one selected calendar.

## Implementation Sequence

1. Product-owner scope sign-off and trust-boundary repairs.
2. SQLite product records, versioned API/action contracts, backup/export/erasure.
3. Graph OAuth, bounded delta ingestion, and selected-calendar reads.
4. Read-only appointment continuity and candidate review gate.
5. Provider-specific calendar reconciliation and verification.
6. Write-enabled UX and a two-week single-user pilot.

## Test Strategy

Use a stratified 120-message gold corpus, deterministic parser/model contract tests, hostile-content tests, restart/forget/model-swap tests, Graph sandbox integration, and create/update/delete unknown-outcome reconciliation tests. The repository web/backend/IaC gates remain mandatory.

## Rollback / Recovery

- Calendar writes remain disabled until the read-only gate passes.
- Connector disconnect deletes the Graph credential and stops both source classes.
- Every schema migration takes a verified backup before pilot data changes.
- Unknown Graph outcomes enter reconciliation rather than blind retry.
- Product records can be exported before rollback; credentials are never included.

## Human Approval Needed

Provider/workflow scope, Graph permission blast radius, model disclosure policy, retention/backup policy, Entra app registration, plan-only PVC/backup changes, write enablement, and pilot consent.
