# Intake

## Understanding

Implement the Tessera R2 usable alpha on the existing R0/R1 .NET modular monolith and React product. Reuse the Kernel evidence, assertion, context, action, authorization, workflow, FollowUp, credential-custody, and SQLite seams; add one unified execution coordinator shared by durable Chat and Jobs.

## Acceptance Criteria

1. Preserve all R0/R1 behavior and additive migrations v1-v4.
2. Map AC-R2-01..46 and Journeys A-J to implementation and executable evidence.
3. Persist owner-scoped accounts, plugin state, model profiles, conversations, public execution events, jobs, runs, leases, grants, and receipts in SQLite without secret values or chain-of-thought.
4. Use real OpenAI-compatible and GitHub REST production transports; fakes remain test-only.
5. Bind exact one-use approvals to owner, action, payload, account, target, plugin/capability versions, and expiry.
6. Recheck plugin/account/grant/policy availability immediately before every Chat or Job dispatch.
7. Serve real `/api/v1` product APIs and compose truthful Chat, Jobs, Accounts, Plugins, Memory, Activity, and Settings UI from Storybook-grounded components.
8. Pass focused tests per phase and final backend, web, Playwright, lint, Storybook, migration, security, dependency, repository, run, and semantic gates.
9. Mark live model/GitHub verification `BLOCKED_BY_EXTERNAL_CREDENTIALS` when configuration is absent; never claim fake completion.
10. Do not commit, branch, deploy/apply IaC, access secrets, use live accounts, or mutate external services.

## Grounding Sources

- `docs/tessera/r1/r2-spec.md`
- `architrave.config.json`, `AGENTS.md`, `knowledge/yagni.md`, `knowledge/backend.md`, `knowledge/learning-loop.md`, `knowledge/web.md`
- R0/R1 contracts under `docs/tessera/r0` and `docs/tessera/r1`, `docs/architecture.md`, and `docs/adr`
- `src/Tessera.Core/Kernel`, `src/Tessera.Persistence.Sqlite`, Broker composition, and their tests
- `docs/ui/tessera-admin-portal-ui-spec.md`, `docs/ui/tessera-design-map.json`, and current Storybook source
- `gates/*.sh`, `harness/*.sh`, and the verified R1 baseline run

## Assumptions

- The user's explicit autonomous-continuation authorization approves all seven listed phases and removes redundant plan/preview approval waits.
- Storybook MCP and configured runtime MCP may be unavailable; repository sources and local deterministic evidence remain authoritative.
- Test transports and Storybook fixtures are allowed only in explicitly test-only surfaces.
- No external credentials or endpoint configuration will be requested or inferred.

## Blocking Questions

None.

## Phase 6 UI Continuation — 2026-08-10

Complete the product UI against the implemented R2 routes without changing external services, secrets, deployment, or unrelated dirty-tree work. The UI must use the exact paged/versioned contract, replace legacy product-route bindings, provide isolated Storybook states before route composition, and close Journeys B, D, E, F, I, and J with focused Vitest and Playwright evidence. The user's explicit implementation authorization continues the existing autonomous Phase 6 approval. `architrave.config.json` advertises Storybook MCP, but `web/.storybook/main.ts` does not register the addon and no callable Storybook MCP tool is exposed, so repository story source is the recorded grounding fallback.
