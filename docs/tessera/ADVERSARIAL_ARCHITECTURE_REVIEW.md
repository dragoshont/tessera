# Adversarial Architecture Review

**Status:** Final independent architecture verdict: PASS, conditional on the documented pre-live gates.

## Findings And Disposition

| Severity | Criticism | Decision |
|---|---|---|
| High | Generic Assertion risks ontology-by-stealth | Assertion is constrained internal R0 state only. R1 uses a workflow-specific FollowUp aggregate. No automatic projection or generic Claim promotion exists. |
| High | Kernel authorization duplicates raw `WriteChallenge` | Kernel contract is the durable generalization target; raw challenge remains the only live egress approval. Convergence is mandatory before live Kernel actions. |
| High | Architecture decisions lacked governance | ADR-001/002/003 and required handoff docs now exist. |
| Medium | `IWorker` duplicated `IModelAdapter` without use | Removed; one replaceable model adapter remains. |
| Medium | Correction persistence depended on caller order | Dedicated atomic correction operation added. |
| Medium | Kernel is uncomposed while deployment has no durable volume | Intentional R0 boundary. Host/PVC/encryption/backup are plan-only R1 decisions; production durability is not claimed. |
| Medium | SQLite could become a dumping ground | Core ports isolate persistence; schema tests exclude credential/policy/security-audit/prompt/model/secret columns. |
| Low | Forward-only migrations lack down path | Additive tables are ignored by prior binaries; destructive rollback requires pre-migration backup in R1. |

## Architecture Verdict

- Core stays dependency-free; SQLite depends inward on Core.
- Evidence, event, assertion, context, action, security audit, policy, and credential responsibilities remain distinct.
- No graph/vector database, provider SDK, cloud model, dynamic plugin loading, or distributed service was introduced.
- Broker remains the trust plane, not canonical product memory.
- Domain IDs and ports permit a future local adapter.
- The R1 FollowUp decision supersedes Appointment only for the current proof slice and
	does not authorize a permanent category, provider, or execution path.

## Hard Gates Before Live Use

1. Ratify host composition and encrypted persistent-volume/backup plan.
2. Migrate legacy policy principals to canonical IDs.
3. Converge Kernel authorization with live out-of-band approval.
4. Add a real provider only after Phase -1 `GO`.