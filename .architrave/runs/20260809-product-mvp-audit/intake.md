# Intake

## Understanding

Audit the shipped Tessera code, tests, portal, architecture records, and deployment examples against Product Vision, Architecture, and Development Specification v0.9. Define a materially smaller MVP that proves the new personal-continuity thesis without discarding the broker's trust assets.

## Acceptance Criteria

- State what the repository actually ships and identify unsupported claims.
- Separate reusable broker capabilities from missing product capabilities.
- Identify security, privacy, persistence, connector, UX, and documentation blockers.
- Compare materially smaller MVP options.
- Recommend one user-visible vertical with explicit non-goals, tests, gates, dependencies, and human decisions.
- Pass repository web, backend, and plan-only infrastructure gates.
- Obtain an independent post-revision semantic verdict.

## Grounding Sources

- `/Users/dragoshont/Downloads/tessera_product_architecture_spec.md` v0.9.
- `architrave.config.json`, `README.md`, `docs/architecture.md`, `docs/roadmap.md`.
- `docs/adr/**`, `docs/specs/**`, `docs/sdd/**`, `docs/ui/**`.
- `src/**`, `tests/**`, `web/src/**`, `web/tests/**`, and `deploy/config/**`.
- Executable gates: `gates/checks.sh` and `gates/backend-checks.sh`.

## Assumptions

- The first pilot may be web-first, self-hosted, single-user, and single-replica.
- Microsoft 365 is available to the pilot user, subject to product-owner approval.
- The existing broker becomes a trust/execution module rather than being discarded.
- No pre-existing personal-knowledge data requires migration.

## Blocking Questions

- Approve Microsoft 365 and appointment continuity as the only MVP provider/workflow.
- Accept or reject Graph's provider-wide delegated permission blast radius.
- Choose deterministic-only or an approved cloud extraction provider.
- Approve the proposed retention, erasure, and backup contract.
- Approve a mandatory read-only gate before calendar writes.
