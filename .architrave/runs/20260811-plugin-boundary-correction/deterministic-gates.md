# Deterministic Gates

**Audited source:** HEAD `723aa31514ed840678bb01aa6b316ea0cfd10902` plus dirty working tree.

**Captured:** `2026-08-11T20:35:56Z`

**Tracked diff SHA-256:** `4b434e19e93ab1e6724ff2258a3fa57ac13178680576b16eb81daaf04640e1dd`

**Untracked source set SHA-256:** `4ba45a37ced17a40aeb1dae0307785ae73c9df5f2aa91ebf1c9fec8ef3648a40`

## checks

Command: `./gates/checks.sh`

Result: **PASS**. Config and design-map JSON valid; web production build passed; Vitest passed 105/105 in 14 files. Existing non-blocking warnings: Architrave copied kit is stale (`0.7.0` versus installed `0.10.3`) and the Vite main chunk is 616.97 kB.

## backend-checks

Command: `./gates/backend-checks.sh`

Result: **PASS**. Backend build passed; .NET tests passed 768/768; plan-only Kustomize render passed; kubeconform found 7 valid resources, 0 invalid/errors/skipped; deployment secret scan found no obvious committed secret.

## reconcile

Command: `./gates/reconcile.sh`

Result: **PASS (not applicable)**. Tokens/tokenBuild are not configured, so the gate reports an explicit skip.

## other

- `npm --prefix web run lint`: PASS.
- `npm --prefix web run build-storybook`: PASS.
- `npm --prefix web run test:e2e`: PASS, 34/34 across desktop and phone, including an arbitrary `calendar-mcp` account/capability grant flow.
- `docker compose -f compose.dev.yaml config --quiet`: PASS.
- `docker build -t tessera:mcp-first-audit .` plus in-image SHA-256 comparison for Gmail/GitHub/RM module DLLs: PASS (`FINAL_DOCKER_MODULES: PASS`).
- `./scripts/devloop/build-plugins` plus catalog/hash checks: PASS.
- isolated host on `127.0.0.1:18080` with egress disabled: readiness PASS; all three modules reached `account_unavailable` rather than `plugin_module_unavailable`, proving load without provider dispatch.
- online backup, isolated restore, integrity/schema verification and unchanged source DB hash: PASS.
- focused final suites: architecture 6/6; MCP protocol/security 6/6; plugin abstractions 18/18; Gmail 9/9; GitHub 8/8; RM 10/10; persistence 70/70; Core 360/360. Drift coverage includes required-field optionalization. The real Broker/RM proposal test proves zero custody reads through proposal and asserts every approval-time read observes the Action in `STARTED`.
- `git diff --check`: PASS; editor diagnostics on touched source trees: no errors.

No deployment, provider authorization, secret access, cluster mutation, restart or real external side effect was performed.
