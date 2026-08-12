# Tessera Overnight Baseline

**Date:** 2026-08-09
**HEAD:** `723aa31` (`2.0-beta`)
**Mission:** Implement the provider-neutral Tessera Kernel v1 authorized by `TESSERA_VSCODE_OVERNIGHT_BUILD_SPEC.md` without treating kernel abstractions as product validation.

## Starting Worktree

Pre-existing untracked paths, preserved throughout this run:

- `.architrave/` — product-audit run and learning artifacts from the preceding task.
- `docs/product-mvp-audit.md` — revised product audit from the preceding task.

No tracked file was dirty before Kernel v1 work.

## Baseline Gates

- `./gates/checks.sh`: PASS.
  - Production React build succeeded.
  - 73 web tests passed, 0 failed.
  - Existing non-blocking bundle-size warning remains.
- `./gates/backend-checks.sh`: PASS.
  - 546 .NET tests passed, 0 failed.
  - Kubernetes render passed.
  - kubeconform: 4 valid resources, 0 invalid/errors/skipped.
  - Deployment secret scan passed.

## Architecture Discoveries

- `Tessera.Core` is dependency-free and owns identity, policy, broker, recipe, audit, and credential-resolution domain logic.
- `Tessera.Broker` is the composition root and ASP.NET host.
- No product-state database or migration framework exists.
- Credential state remains in `ICredentialStore`; policy remains file-first; security audit remains JSONL. Kernel persistence must not absorb any of those responsibilities.
- The raw proxy has content-bound, single-use portal approval; named provider tools still accept caller-controlled `confirm=true`.
- Portal connection creation accepts a raw credential-store key from the client.
- Existing bindings may match mutable `preferred_username` as well as subject.
- `CredentialResolver` snapshots bindings at startup while `PortalService` mutates a different policy snapshot.
- Live-view `postMessage` handling validates payload shape but not origin/source.

## Expected Change Surface

- New provider-neutral domain contracts under `src/Tessera.Core/Kernel/`.
- One SQLite adapter project and one matching test project.
- Focused trust-boundary changes in existing Core/Broker/MCP/web paths.
- Kernel acceptance and adversarial tests using temporary databases and fake capabilities/workers only.
- Documentation under `docs/tessera/` and ADRs under `docs/tessera/adr/`.
- Solution and central package manifest updates.

## Explicit Non-Mutations

- No production or external account access.
- No provider integration.
- No deployment apply.
- No credential migration or secret rotation.
- No branch or commit creation unless requested by the human owner.

## Baseline Interpretation

The overnight kernel is architecture work. Passing its gates will not establish product value, validate a world model, or bypass the Phase -1 and read-only MVP decisions in `docs/product-mvp-audit.md`.