# Tessera R2 R1 Baseline

**Status:** PASS - R2 MAY PROCEED
**Verified:** 2026-08-10

## Repository State

- Branch: `2.0-beta`
- HEAD: `723aa31514ed840678bb01aa6b316ea0cfd10902`
- R0/R1 remain intentionally uncommitted in the current working tree. R2 must preserve and build on that exact tree.
- The canonical R2 specification is `docs/tessera/r1/r2-spec.md`.
- Pre-production-code checkpoint after R2 contract artifacts: 201 porcelain status entries, status SHA-256 `369e1c54f702170d7ef9fed7251a0357adde6984b190f19e5b44e1077a7815236`, tracked binary-diff SHA-256 `1524ef8b2c525a5f6cee2f8ce3a66aac2b56feacb7ab479c088474f49d3d6bcc`. This fingerprints the preserved dirty tree plus contract work without exposing its contents; it is not represented as the earlier R1-only tree.

## Fresh Verification

| Gate | Result |
|---|---|
| `PATH="/usr/local/share/dotnet:$PATH" ./gates/backend-checks.sh` | PASS - 617 backend tests, migrations, Kubernetes render, kubeconform 4/4, deployment secret scan |
| `./gates/checks.sh` | PASS - strict production web build and 94 tests |
| `npm --prefix web run test:e2e` | PASS - 16 desktop/phone browser tests |
| `npm --prefix web run lint` | PASS |
| `npm --prefix web run build-storybook` | PASS |
| `./harness/validate-run.sh .architrave/runs/20260810-r1-continuity` | PASS |
| `git diff --check` | PASS |

R1 product, architecture, security, GPT-family, and Claude-family final verdicts remain PASS. No unresolved Critical or High trust finding blocks R2.

## Verified Substrate

R2 may directly reuse:

- canonical owner identity and owner-scoped repositories;
- Evidence, Observation Events, Assertions, provenance, Context envelopes, Actions, authorizations, and workflow checkpoints;
- additive SQLite migrations and transactional/idempotent persistence;
- FollowUp candidate/current/history/correction/conflict/Why behavior;
- deterministic policy, content-bound approval, credential custody, SSRF-constrained egress, recipe tools, OAuth-MCP acquisition, and session refresh;
- authenticated Broker composition and typed portal API conventions;
- React Query client boundary, Storybook/design map, portal components, and accessibility tests.

## Residual Gates Carried Into R2

| Residual | R2 requirement |
|---|---|
| Kernel authorization and volatile `WriteChallenge` coexist | Converge on durable Kernel Action proposals before Chat/Jobs can perform external effects. |
| Deployment has no approved continuity/product PVC, encryption, backup, or erasure contract | Local dogfood may use an explicit SQLite path; production durability and deletion claims remain blocked. |
| Legacy display-keyed policy remains for compatibility | R2 product state and grants use canonical principals; live deployment requires migration. |
| Obvious-secret validation is not complete DLP | Credentials remain exclusively in the Trust Plane; product tables/logs must reject secret material. |
| R1 browser preview is volatile | R2 production routes must use real APIs/durable state; no preview data may appear as product data. |

## Discrepancies And Non-Claims

- The repository has no production model adapter, persistent Chat, Job scheduler, R2 account/plugin persistence, or integrated Chat-to-capability execution yet.
- Existing recipes are a strong plugin/runtime precursor, not yet a versioned product Plugin SDK.
- Existing portal connections are useful trust-plane bindings, not yet the full R2 `ConnectedAccount` lifecycle/read model.
- Existing pending writes are content-bound but volatile; they do not satisfy durable R2 Action approval and restart requirements.
- Existing OAuth-MCP/provider tests prove protocols using controlled upstreams. They are not evidence that a live user account is configured in this environment.

## Decision

R2 may proceed. It must integrate one modular-monolith Alpha around the existing trust and Kernel seams, not create separate Chat, Job, plugin, or execution engines. Live model and external-service verification may be marked `BLOCKED_BY_EXTERNAL_CREDENTIALS`, but their production implementations may not be replaced by fakes.
