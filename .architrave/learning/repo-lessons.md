# Architrave Repo Lessons

Candidate lessons learned while implementing in this repo. Keep this file short.
Each entry needs evidence and validation before promotion. Do not store secrets.
Promote repeated, stable lessons into `architrave.config.json`, `AGENTS.md`, `.github/instructions/`, or docs after review.

## Candidate Lessons

| Lesson | Evidence | Occurrences | Validated | Proposed Target | Status |
|---|---|---:|---|---|---|
| Audit every external-write path separately; raw proxy and named tools currently use different approval contracts. | `EgressProxyEndpoint.cs`; `ProviderEgress.cs` | 1 | 2026-08-09 | `docs/architecture.md` after implementation | candidate |
| Config example tests are not evidence that a provider connector, OAuth lifecycle, or response contract works live. | `ConnectorsExampleTests.cs`; `grants.connectors.example.json` | 1 | 2026-08-09 | Testing guidance after recurrence | candidate |
| Separate executable validation, semantic design review, and product validation; none substitutes for another. | `docs/product-mvp-audit.md` review status | 1 | 2026-08-09 | `AGENTS.md` after recurrence | candidate |
| Model the experiment domain directly and earn generic structure from measured failures. | Appointment rejected generic Commitment in `docs/product-mvp-audit.md`; FollowUp rejects generic Commitment/Situation/Entity/Claim in run `20260810-r1-continuity` | 2 | 2026-08-10 | `docs/tessera/DOMAIN_MODEL.md` | promotion proposed — approval required |
