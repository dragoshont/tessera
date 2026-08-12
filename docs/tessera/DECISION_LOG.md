# Tessera Kernel Decision Log

## Accepted R0 Decisions

| Decision | Rationale | Consequence |
|---|---|---|
| Keep a modular monolith | No current scaling/isolation evidence justifies services | Logical planes remain modules in existing projects |
| Tessera owns durable state | Model/agent/provider sessions are replaceable | State contracts remain provider/model-neutral |
| Use SQLite behind Core ports | Transactional local/single-node needs are current | No speculative database framework or second store |
| Preserve the broker as trust plane | Existing identity, policy, credential, egress, approval, audit value is real | Kernel cannot bypass broker authority |
| Canonical owner is issuer/tenant/subject | Display email can change/collide | New portal bindings are canonical and self-owned |
| Keep legacy email bindings readable | Existing policy compatibility is required | Legacy matching is isolated and not used for new bindings |
| Define no dedicated prompt/model-output/diagnostics/secret schema columns | Runtime computation is not canonical state | Producer validation must also protect generic text/JSON fields |
| Atomically consume approval and reserve action | Prevent approval loss/replay ambiguity on crash | SQLite updates both in one transaction |
| Constrain generic Assertion | R0 needs current/history semantics but product ontology is unearned | Product slices remain workflow-specific |
| Models and agents are replaceable workers | They must not own memory, policy, or authorization | Small interfaces; no model marketplace/router |
| FollowUp supersedes Appointment as the R1 proof vertical | The Appointment choice was an R0 experiment placeholder; FollowUp better discriminates accepted-context value | R1 uses a workflow-specific FollowUp only; no permanent category or generic Claim is established |
| Kernel authorization is the durable generalization target | Raw WriteChallenge remains the only live egress approval today | Convergence required before live Kernel action; parallel live schemes forbidden |
| Keep Kernel uncomposed in production | Deployment lacks an approved persistent volume/encryption/backup contract | R0 is exercised by deterministic integration tests only |

## Deferred Decisions

- Provider, cloud model, graph/vector store, local-first sync, production topology, backup retention, erasure guarantee, autonomous action policy, and broader product ontology.
- Trusted broker-to-Kernel authorization issuance and provider-specific timeout/reconciliation behavior before live writes.
- Generic Claim/Entity/Situation/Commitment only after measured workflow evidence.

## Product Precedence

The product MVP audit is historical discovery input. R1's accepted vertical decision
is the synthetic, read-only FollowUp continuity proof. It does not validate market
demand or authorize provider integration or execution.

## Review Disposition

Independent R0 findings and remediations are recorded in [security review](ADVERSARIAL_SECURITY_REVIEW.md) and [architecture review](ADVERSARIAL_ARCHITECTURE_REVIEW.md). Final R1 post-remediation verdicts were a completion gate.

R1 product, architecture, security, and two-family final semantic verdicts are PASS.
This validates continuity mechanics only; provider selection, market demand, production
data lifecycle, R2 knowledge evolution, and execution remain separate decisions.