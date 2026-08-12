# Deterministic Gates

## Phase 1

- SQLite migrations/repositories: PASS, 33/33 tests.
- Additive upgrade path: PASS, v1-v4 preserved and v5-v7 repeatable.
- Run artifact validation: PASS.

## Phase 2

- Trusted-local manifest and real adapter tests: PASS, 4/4.
- Registry and dispatch availability tests: PASS, 4/4.
- Credential custody connect/revoke test: PASS, 1/1.
- Live model: BLOCKED_BY_EXTERNAL_CREDENTIALS/ENDPOINT_CONFIGURATION.
- Live GitHub: BLOCKED_BY_EXTERNAL_CREDENTIALS.

## Phase 3

- Core compatibility and Action semantics: PASS, 335/335.
- Shared coordinator restart/substitution/replay: PASS, 1/1.

## Phase 4

- Durable Chat, outage, restart, and SSE replay: PASS.
- Explicit Remember/Correct/Why restart and provenance: PASS.
- Existing bounded context selection/provenance: PASS, 11/11.

## Phase 5

- Durable occurrence uniqueness, restart lease takeover, fencing, and weekday recurrence: PASS, 2/2.

## Phase 6

- Web unit tests: PASS, 100/100.
- ESLint and production TypeScript/Vite build: PASS.
- Storybook production build: PASS.
- Playwright desktop/phone journeys: PASS, 26/26.
- Design reconciliation: PASS; token build is not configured and design-map validation applies.

## Final R2 Deterministic Gate

- Backend build/tests: PASS, 672/672 tests.
- Additive migrations: PASS, v1-v11 including exact Chat recovery metadata and conversation grants.
- IaC plan/policy: PASS, 4/4 rendered resources valid; no apply.
- Deploy secret scan: PASS.
- Web build/tests: PASS, 100 tests.
- ESLint and Storybook: PASS.
- Playwright desktop/phone: PASS, 26 tests.
- Design reconciliation: PASS (token build not configured).
- Structural PII/secret scan: PASS.
- Diff integrity: PASS.

## checks

R2 final deterministic UI gate: 100 web tests, production build, lint, Storybook, and 26 Playwright tests PASS.

## backend-checks

R2 final backend gate: 672 tests PASS; Kubernetes render and kubeconform 4/4 PASS; deploy secret scan PASS.

## reconcile

R2 reconciliation PASS; tokens/tokenBuild are not configured.

## other

Runtime observation was not used. No deployment, reconcile, restart, secret access, live account use, or external mutation was authorized or performed.
