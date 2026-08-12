# Tessera R2.1 Baseline Verification

**Captured:** 2026-08-10
**Branch:** `2.0-beta`
**HEAD:** `723aa31514ed840678bb01aa6b316ea0cfd10902`

## Starting State

The worktree intentionally contains the uncommitted R0/R1/R2 implementation and pre-existing broker work. R2.1 treats all of it as source material. No reset, clean, checkout, rebase, commit, deployment, secret read, or external mutation was performed.

The baseline contains more than one hundred modified or untracked paths. The final R2 gate immediately before this phase passed with:

- backend: 672/672 tests;
- web: 100/100 tests;
- Playwright: 26/26 desktop and phone journeys;
- ESLint, production build, Storybook, Compose rendering, Kubernetes rendering/policy, secret scan, Architrave run validation, reconciliation, and diff integrity: PASS;
- Product, Architecture, and Security semantic reviews: PASS.

## Verified R2 Capabilities

- The Broker hosts the built SPA, SQLite product store, scheduler, plugin catalog, account custody integration, Chat worker, and `/api/v1` product API.
- Chat acceptance, retry, Stop, SSE events, restart recovery, conversation grants, and owner isolation are durable.
- Explicit Memory remember/correct/Why uses Tessera Evidence and Assertions.
- Jobs use durable schedules, leases, fencing, scoped account/capability grants, context snapshots, Actions, outputs, Evidence, and restart recovery.
- Consequential calls use exact, expiring, single-use Action authorization and reconciliation.
- Trusted-local plugins are declarative, version/hash pinned, bounded, and fail closed when disabled.
- Model and GitHub adapters are production implementations with contract tests; they are not fixture success paths.

## Runtime Reality

`./scripts/devloop/up` is the existing authoritative loopback development path. It starts Lowkey, seeds development-only bundles, materializes `.dev` configuration, builds the SPA when absent, and starts the Broker at `http://localhost:8080` with the local developer sign-in.

`docker compose -f compose.dev.yaml up --build` starts `lowkey` and `tessera`, bakes the SPA and plugins, and persists `/data` in `tessera-product`. It intentionally uses OIDC because container binding is non-loopback; it is not a clean-state zero-config dogfood sign-in path.

## Discrepancies And R2.1 Work

- No dedicated Alpha health/readiness projection covers database, scheduler, plugin registry, model configuration, and connected accounts.
- No supported product-database backup/restore command and isolated restore verification exist.
- No opt-in live-provider harness records model/GitHub `PASS`, `FAIL`, or `BLOCKED_EXTERNAL` independently.
- Runtime documentation is operator/cluster-oriented and does not yet provide one concise Alpha dogfood path.
- The complete R2.1 documentation and reality-shaped regression matrix do not yet exist.
- Live model setup has no credential available in the current environment.
- GitHub CLI is authenticated as `dragoshont`, but no Tessera GitHub credential/account is configured and no live write target or write opt-in is present.
- VS Code reports stale TypeScript configuration-schema warnings for supported TypeScript 6 `ES2023`; compiler/build truth is green.

## External Blockers

| Check | Baseline status | Required external input |
|---|---|---|
| OpenAI-compatible live Chat/tool call | `BLOCKED_EXTERNAL` | endpoint, model ID, credential entered through Tessera custody |
| GitHub identity/read | `BLOCKED_EXTERNAL` | credential connected through Tessera and an allow-listed repository |
| GitHub safe write | `NOT_RUN_SAFE_MODE` | explicit write opt-in and named non-production repository |

Contract verification and all independent engineering continue despite these blockers.

## Expected R2.1 Change Areas

- `scripts/devloop/**`, `compose.dev.yaml`, runtime configuration examples;
- Broker health/readiness and persistence/backup support;
- opt-in live Alpha verification gates;
- reality-shaped provider, restart, scheduler, cancellation, and recovery tests;
- product setup/recovery UX where current state is not actionable;
- `docs/tessera/r2_1/**` and Architrave R2.1 run evidence.