# Architrave Repo Profile

Concise, validated repository description for future Architrave runs. Keep this high-signal and cite evidence; move detailed rules into docs or path-scoped instructions.

## Purpose

Tessera implements a user-operated, identity-aware credential/action broker and R2 usable Alpha: durable Chat, explicit Memory, connected accounts, declarative plugins, exact Actions, and fenced Jobs. Live model/GitHub verification remains owner-configured and `BLOCKED_EXTERNAL`.

## Surfaces And Lanes

- Backend: .NET 10 modular solution under `src/**` and `tests/**`.
- Web: React/Vite portal under `web/**`.
- Deployment: Kubernetes/config/Entra material under `deploy/**`; apply remains human-owned.

## Source Of Truth

- Current broker: `docs/architecture.md`, ADRs, code, and tests.
- R2 product contract: `docs/tessera/r1/r2-spec.md`, `docs/tessera/r2/**`, and the complete `architrave.config.json.backend.contracts` set.
- UI source: Storybook and `docs/ui/tessera-admin-portal-ui-spec.md` for the existing admin portal.

## Build And Test

- Web: `./gates/checks.sh`.
- Backend and plan-only IaC: `./gates/backend-checks.sh`.
- Run artifacts: `./harness/validate-run.sh <run-dir>`.

## Architecture Map

- `Tessera.Core`: policy, bindings, recipes, audit, health, broker primitives, and provider-neutral Kernel contracts/ports.
- `Tessera.Persistence.Sqlite`: additive v1-v15 Kernel/product schema, generic plugin cursors, MCP runtime receipts, owner-scoped repositories, conversation/Job grants, execution traces, leases, and recovery.
- `Tessera.Identity`: Entra OIDC validation.
- `Tessera.Providers`: provider-neutral HTTP and model transport primitives.
- `Tessera.Broker`: provider-neutral host, product endpoints, portal, guarded egress, policy and approvals.
- `Tessera.Mcp`: inbound MCP surface; `Tessera.Mcp.Client` is the outbound Streamable HTTP runtime.
- `Tessera.Plugin.Abstractions`: executable plugin, account, host, model-tool and typed MCP compatibility contracts.
- `Tessera.Plugins.Gmail`, `.GitHub`, `.ReginaMaria`: optional hash-pinned provider modules; neutral projects do not reference them.
- `Tessera.Stores.AzureKeyVault`: credential store.
- `web`: connection/admin portal.

## Recurring Gotchas

- Raw proxy writes use content-bound portal approval; named provider writes now remain held even when the legacy caller-controlled `confirm=true` flag is supplied.
- Connector example tests prove configuration shape/policy, not live provider behavior or result shaping.
- Portal policy mutation now replaces the live `CredentialResolver` snapshot; refresh orchestration reads current policy.
- Product SQLite is composed explicitly in Broker and owns durable Chat/Memory/Actions/Jobs metadata; credentials remain in custody stores.
- R1 continuity SQLite is composed only when an explicit local path is supplied;
	deployment manifests and PVC claims remain unchanged.
- Product validation is distinct from executable gates and model design review.
- Phase 0 trust repair precedes any active personal-source ingestion.
- Product aggregates remain workflow-specific; R1 uses FollowUp and generic world-model structure must still be earned.

## Validated Facts

| Fact | Evidence | Last Checked |
|---|---|---|
| Production host builds only an OIDC validator; mTLS/SVID is not hosted | `src/Tessera.Broker/BrokerHost.cs` | 2026-08-09 |
| Graph examples are not a complete connector | `deploy/config/grants.connectors.example.json`, `src/Tessera.Providers/SessionRefresher.cs` | 2026-08-09 |
| Web and backend deterministic gates pass | `.architrave/runs/20260809-product-mvp-audit/deterministic-gates.md` | 2026-08-09 |
| FollowUp is the R1 proof vertical only; execution remains outside R1 | `docs/tessera/r1/CONTINUITY_VERTICAL_DECISION.md` | 2026-08-10 |
| No cloud LLM receives email content in MVP | `docs/product-mvp-audit.md` | 2026-08-09 |
| Kernel schema has no dedicated prompt/model-output/diagnostics/secret columns; generic fields still need content validation | `src/Tessera.Persistence.Sqlite/KernelMigrations.cs`, `tests/Tessera.Persistence.Sqlite.Tests/SqliteMigrationTests.cs` | 2026-08-10 |
| Authorization consume and `PROPOSED` to `AUTHORIZED` reservation share one SQLite transaction | `src/Tessera.Persistence.Sqlite/SqliteKernelStore.Execution.cs` | 2026-08-10 |
| Named writes ignore legacy caller confirmation; portal bindings use canonical self-owner and generated credential refs | provider/broker source and focused tests | 2026-08-10 |
| FollowUp correction/context/conflict/replay/completion persists across SQLite restart and remains owner-isolated | `FollowUpContinuityTests.cs`; run `20260810-r1-continuity` | 2026-08-10 |
| Full R1 gates pass at 617 backend, 94 web, and 16 browser tests; all final adversaries PASS | `.architrave/runs/20260810-r1-continuity/deterministic-gates.md`, `judge-post.md` | 2026-08-10 |
| R2 internal/e2e gates pass at 672 backend, 100 web, and 26 browser tests; Product/Architecture/Security PASS | `.architrave/runs/20260810-r2-usable-alpha/deterministic-gates.md`, `judge-post.md` | 2026-08-10 |
| MCP-first repository gates pass at 768 backend, 105 web, and 34 browser tests; architecture/security pass in Copilot and Claude families; repository product UX passes | `.architrave/runs/20260811-plugin-boundary-correction/deterministic-gates.md`, `judge-post.md` | 2026-08-11 |
| Corrected image is not deployed; Gmail/RM authorization, real provider actions and deployed restart/recovery remain blocked human/runtime phases | `docs/tessera/delivery/FINAL_DELIVERY_REPORT.md` | 2026-08-11 |

## Last Reviewed

2026-08-11
