# Adversarial Security Review

**Status:** Final independent review found no remaining critical/high R0 blocker. Admin-contract documentation drift was corrected after a `REVISE` verdict.

## Findings And Disposition

| Severity | Finding | Mitigation | Status |
|---|---|---|---|
| Critical | Approved action payload was not bound to actual capability input | Repository-backed dispatcher hashes actual JSON and atomically checks owner/action/version/auth/capability/target/idempotency/payload while reserving `STARTED` | Fixed; swap/target/replay tests |
| Critical | Tenant-aware users could fall back to email-keyed grants/bindings | Canonical issuer/tenant/subject ID; real tenant-aware tokens cannot use legacy fallback; raw approval/OAuth state use canonical IDs | Fixed; Core/Broker/Egress/OAuth/MCP tests |
| High | Action lifecycle bypass through direct persistence | New inserts require `PROPOSED` v0; updates require a valid transition from durable state; factory validates structural invariants | Fixed |
| High | Caller boolean self-approved named writes | Boolean is compatibility-only; named side effects remain step-up pending independent approval | Fixed |
| High | Browser selected raw credential-store key | API/UI field removed; opaque server refs; self-owned canonical creation | Fixed |
| High | Live-view accepted arbitrary window/origin messages | Exact iframe source window and URL origin required | Fixed |
| Medium | Context ID culture-sensitive | Invariant decimal/timestamp formatting | Fixed |
| Medium | Assertion temporal inversion accepted | Domain validation rejects inverted intervals | Fixed |
| Medium | Correction ordering caller-dependent | Atomic `ApplyCorrectionAsync`; current-last batch ordering | Fixed |
| Medium | Hostile evidence claimed authority | Side effects require repository-backed authorization; regression proves no invocation | Fixed |
| Medium | Obvious secret material could enter durable excerpts/receipts | Fail-closed obvious-secret validation plus schema column guard | Fixed for obvious patterns; full DLP not claimed |

## Residual Risks

- Legacy display-keyed policies remain readable for loopback dev and identity sources without canonical tenant context. Tenant-aware signed users fail closed and require policy migration.
- Kernel authorization and raw `WriteChallenge` coexist during R0, but Kernel actions are not wired to live egress. They must converge before any live Kernel execution.
- OIDC `portal.admins` entries are canonical principal IDs. Email, preferred username, and bare subject entries fail configuration validation.
- Product backup/restore/erasure and complete content DLP remain future work.
- No production or external account was exercised.

## Gate Evidence

- 599 backend tests passed.
- 74 web tests passed.
- Kubernetes render and kubeconform passed for four resources.
- Deployment secret scan passed.
- NuGet direct/transitive vulnerability audit found no vulnerable packages.